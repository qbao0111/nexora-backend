using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class AuthPostgresApiTests
{
    [PostgresFact]
    public async Task PostgresVerificationAndSessionSwitchRegressionUsesDatabaseBackedOwnership()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        await using var factory = NexoraApiFactory.CreatePostgres(connectionString);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var emailA = $"postgres-auth-a-{Guid.NewGuid():N}@example.test";
        var emailB = $"postgres-auth-b-{Guid.NewGuid():N}@example.test";

        using var registerA = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = emailA, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.Created, registerA.StatusCode);
        var tokenA = ReadVerificationToken(TestEmailInbox.GetVerificationLinks(emailA)[0]);
        using var resendA = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new { email = emailA });
        Assert.Equal(HttpStatusCode.OK, resendA.StatusCode);
        Assert.Equal(2, TestEmailInbox.GetVerificationLinks(emailA).Count);

        using var verifyA = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new
        {
            userId = tokenA.UserId,
            token = tokenA.Token
        });
        Assert.Equal(HttpStatusCode.OK, verifyA.StatusCode);
        await RegisterAndVerifyAsync(client, emailB);

        using var loginA = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = emailA, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, loginA.StatusCode);
        var accessTokenA = await ReadAccessTokenAsync(loginA);
        var refreshTokenA = ReadRefreshCookie(loginA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessTokenA);
        using var meA = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, meA.StatusCode);
        Assert.Equal(emailA, await ReadSessionEmailAsync(meA));

        using var logout = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;

        using var replayClient = factory.CreateHttpsClient(handleCookies: false);
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        replayRequest.Headers.Add("Cookie", $"nexora.refresh={refreshTokenA}");
        using var replay = await replayClient.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var loginB = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = emailB, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, loginB.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await ReadAccessTokenAsync(loginB));
        using var meB = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, meB.StatusCode);
        Assert.Equal(emailB, await ReadSessionEmailAsync(meB));

        using var refreshB = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshB.StatusCode);
        Assert.Equal(emailB, await ReadSessionEmailAsync(refreshB));
    }

    private static async Task RegisterAndVerifyAsync(HttpClient client, string email)
    {
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
    }

    private static (Guid UserId, string Token) ReadVerificationToken(Uri link)
    {
        var query = link.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => WebUtility.UrlDecode(part[0]),
                part => WebUtility.UrlDecode(part.ElementAtOrDefault(1) ?? string.Empty),
                StringComparer.Ordinal);
        return (Guid.Parse(query["userId"]), query["token"]);
    }

    private static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static async Task<string> ReadSessionEmailAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return data.TryGetProperty("user", out var sessionUser)
            ? sessionUser.GetProperty("email").GetString()!
            : data.GetProperty("email").GetString()!;
    }

    private static string ReadRefreshCookie(HttpResponseMessage response)
    {
        var cookie = response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(value => value.StartsWith("nexora.refresh=", StringComparison.Ordinal))
            : null;
        Assert.NotNull(cookie);
        return cookie!.Split(';', 2)[0].Split('=', 2)[1];
    }
}
