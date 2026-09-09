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
    public async Task RegisterRequiresEmailVerificationBeforeLoginAndCurrentUserFlowWorks()
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
        Assert.DoesNotContain(register.Headers, header => string.Equals(header.Key, "Set-Cookie", StringComparison.OrdinalIgnoreCase));
        using (var registrationBody = JsonDocument.Parse(await register.Content.ReadAsStringAsync()))
        {
            var data = registrationBody.RootElement.GetProperty("data");
            Assert.Equal(email, data.GetProperty("email").GetString());
            Assert.True(data.GetProperty("verificationRequired").GetBoolean());
        }

        using var beforeVerification = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.Unauthorized, beforeVerification.StatusCode);
        using (var error = JsonDocument.Parse(await beforeVerification.Content.ReadAsStringAsync()))
            Assert.Equal("EMAIL_NOT_VERIFIED", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var beforeVerificationAi = await client.PostAsJsonAsync("/api/v1/interviews", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, beforeVerificationAi.StatusCode);

        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("nexora.refresh=", StringComparison.Ordinal));

        var accessToken = await ReadAccessTokenAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(email, body.RootElement.GetProperty("data").GetProperty("email").GetString());
    }

    [Fact]
    public async Task RefreshRotatesTokenAndRejectsPreviousToken()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"rotate-{Guid.NewGuid():N}@example.test";
        await RegisterAndVerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        var oldToken = ExtractRefreshToken(Assert.Single(login.Headers.GetValues("Set-Cookie")));
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
        var email = $"logout-{Guid.NewGuid():N}@example.test";
        await RegisterAndVerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        var accessToken = await ReadAccessTokenAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var logout = await client.PostAsync("/api/v1/auth/logout-all", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        using var refresh = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task VerificationRetryDoesNotDuplicateFreeEntitlement()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"entitlement-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Entitlement candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        Guid userId;
        using (var beforeScope = _factory.Services.CreateScope())
        {
            var userManager = beforeScope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Nexora.Data.Identity.ApplicationUser>>();
            var user = await userManager.FindByEmailAsync(email);
            Assert.NotNull(user);
            userId = user.Id;
            Assert.False(user.EmailConfirmed);
            Assert.Empty(await beforeScope.ServiceProvider.GetRequiredService<NexoraDbContext>().Entitlements.Where(item => item.UserId == user.Id).ToListAsync());
        }

        await TestEmailInbox.VerifyAsync(client, email);
        var verificationLink = TestEmailInbox.GetVerificationLink(email);
        var query = verificationLink.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(part => Uri.UnescapeDataString(part[0]), part => Uri.UnescapeDataString(part.ElementAtOrDefault(1) ?? string.Empty), StringComparer.Ordinal);
        using var retry = await client.PostAsJsonAsync("/api/v1/auth/verify-email", new
        {
            userId,
            token = query["token"]
        });
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);

        using var afterScope = _factory.Services.CreateScope();
        var db = afterScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Single(await db.Entitlements.Where(item => item.UserId == userId && item.PlanCodeSnapshot == "free").ToListAsync());
        Assert.Single(await db.Subscriptions.Where(item => item.UserId == userId).ToListAsync());
    }

    [Fact]
    public async Task ResendVerificationReturnsGenericResponseAndIsRateLimited()
    {
        await using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:LoginEmail:PermitLimit"] = "1",
            ["RateLimits:LoginEmail:WindowMinutes"] = "15"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var email = $"resend-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        using var resend = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new { email });
        Assert.Equal(HttpStatusCode.OK, resend.StatusCode);
        using var unknown = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new { email = $"missing-{Guid.NewGuid():N}@example.test" });
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(await resend.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());

        using var limited = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new { email });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task ForgotPasswordReturnsSameGenericResponseForKnownAndUnknownEmail()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"forgot-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        using var known = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        using var unknown = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = $"missing-{Guid.NewGuid():N}@example.test" });
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Equal(await known.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.NotNull(TestEmailInbox.GetPasswordResetLink(email));
    }

    [Fact]
    public async Task PasswordResetRevokesSessionsAndRequiresTheNewPassword()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"reset-{Guid.NewGuid():N}@example.test";
        await RegisterAndVerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var accessToken = await ReadAccessTokenAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var forgot = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.OK, forgot.StatusCode);
        await TestEmailInbox.ResetPasswordAsync(client, email, "New!StrongPass456");

        using var oldAccess = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldAccess.StatusCode);
        using var oldRefresh = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);

        using (var oldPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" }))
            Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Nexora.Data.Identity.ApplicationUser>>();
        var user = await userManager.FindByEmailAsync(email);
        Assert.NotNull(user);
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.All(await db.RefreshTokens.Where(token => token.UserId == user.Id).ToListAsync(), token => Assert.NotNull(token.RevokedAt));

        using var newPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "New!StrongPass456" });
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [Fact]
    public async Task InvalidPasswordResetTokenUsesSafeErrorForExistingAndUnknownUser()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"invalid-reset-{Guid.NewGuid():N}@example.test";
        await RegisterAndVerifyAsync(client, email);
        Guid userId;
        using (var scope = _factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<Nexora.Data.Identity.ApplicationUser>>();
            userId = (await userManager.FindByEmailAsync(email))!.Id;
        }

        using var existing = await client.PostAsJsonAsync("/api/v1/auth/reset-password", new
        {
            userId,
            token = "invalid-token",
            newPassword = "New!StrongPass456"
        });
        using var unknown = await client.PostAsJsonAsync("/api/v1/auth/reset-password", new
        {
            userId = Guid.NewGuid(),
            token = "invalid-token",
            newPassword = "New!StrongPass456"
        });
        Assert.Equal(HttpStatusCode.BadRequest, existing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(await ReadErrorCodeAsync(existing), await ReadErrorCodeAsync(unknown));
        Assert.Equal("PASSWORD_RESET_INVALID", await ReadErrorCodeAsync(existing));
    }

    [Fact]
    public async Task ForgotPasswordRateLimitsNormalizedEmail()
    {
        await using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:LoginEmail:PermitLimit"] = "1",
            ["RateLimits:PasswordRecovery:PermitLimit"] = "1000"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var email = $"normalized-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);

        using var first = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = email.ToUpperInvariant() });
        using var second = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task ForgotPasswordRateLimitsClientIpAcrossDifferentEmails()
    {
        await using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:LoginEmail:PermitLimit"] = "1000",
            ["RateLimits:PasswordRecovery:PermitLimit"] = "1"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        using var first = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = $"ip-one-{Guid.NewGuid():N}@example.test" });
        using var second = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { email = $"ip-two-{Guid.NewGuid():N}@example.test" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
    }

    [Fact]
    public async Task ChangePasswordRevokesSessionsAndRequiresTheNewPassword()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"change-password-{Guid.NewGuid():N}@example.test";
        await RegisterAndVerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var accessToken = await ReadAccessTokenAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var change = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "Strong!Pass123",
            newPassword = "Changed!Pass456"
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        using var oldAccess = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Unauthorized, oldAccess.StatusCode);
        using var oldRefresh = await client.PostAsync("/api/v1/auth/refresh", null);
        Assert.Equal(HttpStatusCode.Unauthorized, oldRefresh.StatusCode);
        using var oldPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.Unauthorized, oldPassword.StatusCode);
        using var newPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Changed!Pass456" });
        Assert.Equal(HttpStatusCode.OK, newPassword.StatusCode);
    }

    [Fact]
    public async Task ChangePasswordRejectsIncorrectCurrentPasswordWithoutRevokingSession()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"change-password-invalid-current-{Guid.NewGuid():N}@example.test";
        await RegisterAndVerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        var accessToken = await ReadAccessTokenAsync(login);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var change = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "Wrong!Pass123",
            newPassword = "Changed!Pass456"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, change.StatusCode);
        Assert.Equal("INCORRECT_CURRENT_PASSWORD", await ReadErrorCodeAsync(change));

        using var me = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        using var oldPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, oldPassword.StatusCode);
    }

    [Fact]
    public async Task ChangePasswordRejectsPasswordShorterThanMinimum()
    {
        using var client = _factory.CreateHttpsClient();
        var email = $"change-password-invalid-new-{Guid.NewGuid():N}@example.test";
        await RegisterAndVerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await ReadAccessTokenAsync(login));

        using var change = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "Strong!Pass123",
            newPassword = "short"
        });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ReadErrorCodeAsync(change));
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

    private static async Task RegisterAndVerifyAsync(HttpClient client, string email)
    {
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
    }

    private static async Task<string> ReadAccessTokenAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static string ExtractRefreshToken(string cookie) =>
        cookie.Split(';', 2)[0].Split('=', 2)[1];

    private static async Task<string> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }
}
