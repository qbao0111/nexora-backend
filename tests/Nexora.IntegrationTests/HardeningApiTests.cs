using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Nexora.IntegrationTests;

public sealed class HardeningApiTests
{
    [Fact]
    public async Task LoginEmailLimitReturnsCanonical429IndependentlyFromIpLimit()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:Authentication:PermitLimit"] = "100",
            ["RateLimits:LoginEmail:PermitLimit"] = "2",
            ["RateLimits:LoginEmail:WindowMinutes"] = "15"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var rejectedCredentials = await client.PostAsJsonAsync("/api/v1/auth/login", new
            {
                email = "distributed-target@example.test",
                password = "Wrong!Pass123"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedCredentials.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = "distributed-target@example.test",
            password = "Wrong!Pass123"
        });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter is not null || limited.Headers.TryGetValues("Retry-After", out _));
        using var payload = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("RATE_LIMITED", payload.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task AuthenticationRateLimitReturnsCanonical429AndRetryAfter()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:Authentication:PermitLimit"] = "2",
            ["RateLimits:Authentication:WindowMinutes"] = "15"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var rejectedLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "missing@example.test", password = "Wrong!Pass123" });
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedLogin.StatusCode);
        }

        using var limited = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "missing@example.test", password = "Wrong!Pass123" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var values));
        Assert.True(int.Parse(Assert.Single(values), System.Globalization.CultureInfo.InvariantCulture) > 0);
        using var payload = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal("RATE_LIMITED", payload.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task WhenRateLimitsDisabledRequestsAreNotRateLimited()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:Disabled"] = "true",
            ["RateLimits:Authentication:PermitLimit"] = "1",
            ["RateLimits:Authentication:WindowMinutes"] = "15",
            ["RateLimits:LoginEmail:PermitLimit"] = "1",
            ["RateLimits:LoginEmail:WindowMinutes"] = "15"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var rejectedLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "missing@example.test", password = "Wrong!Pass123" });
            Assert.Equal(HttpStatusCode.Unauthorized, rejectedLogin.StatusCode);
        }
    }

    [Fact]
    public async Task IndependentFeatureGatesPreserveAuthenticationBoundary()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["Features:Ai"] = "false",
            ["Features:Payment"] = "false",
            ["Features:Upload"] = "false"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        using (var guest = await client.PostAsJsonAsync("/api/v1/interviews", new { }))
            Assert.Equal(HttpStatusCode.Unauthorized, guest.StatusCode);

        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"hardening-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "Hardening candidate"
        });
        using var document = JsonDocument.Parse(await register.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            document.RootElement.GetProperty("data").GetProperty("accessToken").GetString());

        await AssertDisabledAsync(await client.PostAsJsonAsync("/api/v1/interviews", new { }));
        await AssertDisabledAsync(await client.PostAsJsonAsync("/api/v1/checkout-sessions", new { }));
        await AssertDisabledAsync(await client.PostAsJsonAsync("/api/v1/uploads/presign", new { }));
    }

    private static async Task AssertDisabledAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("FEATURE_DISABLED", payload.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
    }
}
