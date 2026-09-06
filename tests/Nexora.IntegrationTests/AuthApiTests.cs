using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class AuthApiTests : IClassFixture<NexoraApiFactory>
{
    private readonly NexoraApiFactory _factory;

    public AuthApiTests(NexoraApiFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    [Fact]
    public async Task RegisterLoginAndCurrentUserFlowWorks()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"candidate-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var cookie = Assert.Single(register.Headers.GetValues("Set-Cookie"));
        Assert.Contains("HttpOnly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SameSite=Strict", cookie, StringComparison.OrdinalIgnoreCase);

        var accessToken = await ReadAccessTokenAsync(register);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(email, body.RootElement.GetProperty("data").GetProperty("email").GetString());

        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task RefreshRotatesTokenAndRejectsPreviousToken()
    {
        using var client = _factory.CreateHttpsClient();
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"rotate-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "Rotate"
        });
        var oldToken = ExtractRefreshToken(Assert.Single(register.Headers.GetValues("Set-Cookie")));
        using var refresh = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var newToken = ExtractRefreshToken(Assert.Single(refresh.Headers.GetValues("Set-Cookie")));
        Assert.NotEqual(oldToken, newToken);

        using var replayClient = _factory.CreateHttpsClient(handleCookies: false);
        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/refresh");
        replayRequest.Headers.Add("Cookie", $"nexora.refresh={oldToken}");
        using var replay = await replayClient.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var tokens = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().RefreshTokens.ToListAsync();
        Assert.Contains(tokens, token => token.RevokedAt is not null && token.ReplacedByTokenHash is not null);
        Assert.Contains(tokens, token => token.RevokedAt is null);
    }

    [Fact]
    public async Task LogoutAllInvalidatesAccessAndRefreshTokens()
    {
        using var client = _factory.CreateHttpsClient();
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"logout-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "Logout"
        });
        var accessToken = await ReadAccessTokenAsync(register);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var logout = await client.PostAsync("/api/v1/auth/logout-all", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var refresh = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task SameOriginAuthMutationIsPermittedForInternalClients()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"sameorigin-{Guid.NewGuid():N}@example.test";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new { email, password = "Strong!Pass123", displayName = "Same Origin" })
        };
        request.Headers.Add("Origin", "https://localhost");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task WhitelistedFrontendOriginIsPermitted()
    {
        await using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["Frontend:AllowedOrigins:0"] = "http://localhost:5173"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var email = $"whitelisted-{Guid.NewGuid():N}@example.test";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = JsonContent.Create(new { email, password = "Strong!Pass123", displayName = "Allowed Origin" })
        };
        request.Headers.Add("Origin", "http://localhost:5173");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task UntrustedOriginIsRejectedWithCsrfForbiddenEnvelope()
    {
        using var client = _factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new { email = "test@example.com", password = "Password123!" })
        };
        request.Headers.Add("Origin", "https://malicious-phishing.example");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("CSRF_ORIGIN_INVALID", error.GetProperty("code").GetString());
    }

    private static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static string ExtractRefreshToken(string cookie) =>
        cookie.Split(';', 2)[0].Split('=', 2)[1];
}
