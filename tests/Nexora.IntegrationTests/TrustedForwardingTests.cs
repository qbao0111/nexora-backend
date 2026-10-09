using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Api.Infrastructure;

namespace Nexora.IntegrationTests;

public sealed class TrustedForwardingTests
{
    [Fact]
    public async Task AuthenticatedUsersBehindSameNatHaveIndependentExpensiveOperationLimits()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimits:Upload:PermitLimit"] = "2"
        }).Build();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHardening(configuration);
        await using var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.1");
            // Test-only stand-in for the principal established by JWT authentication.
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", context.Request.Headers["Test-User"].ToString())], "Test"));
            return next(context);
        });
        app.UseRateLimiter();
        app.MapPost("/upload", () => Results.Ok()).RequireRateLimiting(RateLimitPolicies.Upload);
        await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var user in new[] { "first-user", "second-user" })
        {
            for (var index = 0; index < 3; index++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "/upload");
                request.Headers.Add("Test-User", user);
                using var response = await client.SendAsync(request);
                Assert.Equal(index < 2 ? HttpStatusCode.OK : HttpStatusCode.TooManyRequests, response.StatusCode);
            }
        }
    }
    [Theory]
    [InlineData("203.0.113.10", "198.51.100.42", "198.51.100.42")]
    [InlineData("203.0.113.10", "2001:db8::42", "2001:db8::42")]
    [InlineData("203.0.113.11", "198.51.100.42", "203.0.113.11")]
    [InlineData("203.0.113.10", "not-an-ip", "203.0.113.10")]
    [InlineData("203.0.113.10", "192.0.2.99, 198.51.100.42", "198.51.100.42")]
    [InlineData("203.0.113.10", "1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17", "203.0.113.10")]
    public async Task OnlyExplicitProxyCanSetValidatedClientIp(string peer, string forwarded, string expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:KnownProxies:0"] = "203.0.113.10"
        }).Build();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.Configure<ForwardedHeadersOptions>(options => TrustedForwarding.Configure(options, configuration));
        await using var app = builder.Build();
        app.Use((context, next) =>
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
                    return next(context);
                });
        app.UseMiddleware<ForwardedHeaderBoundsMiddleware>();
        app.UseForwardedHeaders();
        app.Run(context => context.Response.WriteAsync(TrustedForwarding.ClientIp(context)));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", forwarded);
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", "192.0.2.123");
        Assert.Equal(expected, await client.GetStringAsync("/"));
    }

    [Fact]
    public async Task RotatingForgedHeadersCannotBypassAuthenticationLimit()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:Authentication:PermitLimit"] = "2"
        }, services => services.AddSingleton<IStartupFilter, UntrustedPeerFilter>());
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        for (var index = 0; index < 3; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { email = "absent@example.test", password = "Wrong!Pass123" })
            };
            request.Headers.Add("X-Forwarded-For", $"198.51.100.{index + 1}");
            request.Headers.Add("CF-Connecting-IP", $"192.0.2.{index + 1}");
            using var response = await client.SendAsync(request);
            Assert.Equal(index < 2 ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, response.StatusCode);
            if (index == 2) Assert.NotNull(response.Headers.RetryAfter);
        }
    }

    [Fact]
    public void ProductionCannotDisableRateLimits()
    {
        using var factory = new NexoraApiFactory("Production", new Dictionary<string, string?> { ["RateLimits:Disabled"] = "true" });
        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateHttpsClient());
        Assert.Contains("RateLimits:Disabled", error.Message);
    }

    [Fact]
    public async Task JsonBodyAboveLimitIsRejectedBeforeAuthenticationWork()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        using var body = new StringContent(new string('x', 1024 * 1024 + 1), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/v1/auth/login", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private sealed class UntrustedPeerFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.11");
                return continuation(context);
            });
            next(app);
        };
    }
}
