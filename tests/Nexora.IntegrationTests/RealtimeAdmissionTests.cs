using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Api.Infrastructure;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class RealtimeAdmissionTests
{
    [Fact]
    public Task SustainedAuthenticatedSocketsDoNotStarveHttpOrHealth() => RunSocketsAsync(false);

    [PostgresFact]
    public Task SustainedAuthenticatedSocketsDoNotStarveHttpOrHealthOnPostgres() => RunSocketsAsync(true);

    private static async Task RunSocketsAsync(bool postgres)
    {
        using var factory = CreateFactory(postgres);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var a = await RealtimeApiTests.RegisterAsync(client);
        var b = await RealtimeApiTests.RegisterAsync(client);
        using var first = await RealtimeApiTests.ConnectAsync(factory, a.Token);
        using var second = await RealtimeApiTests.ConnectAsync(factory, b.Token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.Token);
        // Two users behind one NAT hold more sockets than the regular HTTP cap (1).
        for (var wave = 0; wave < 5; wave++)
        {
            var api = client.GetAsync("/api/v1/me");
            var live = client.GetAsync("/health/live");
            var ready = client.GetAsync("/api/v1/health");
            foreach (var response in await Task.WhenAll(api, live, ready))
            {
                using (response) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }
        Assert.Equal(WebSocketState.Open, first.Socket.State);
        Assert.Equal(WebSocketState.Open, second.Socket.State);
        using var callback = await client.GetAsync("/api/v1/webhooks/payments/payos");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, callback.StatusCode); // Routing reached, not concurrency 429; no payment mutation.
        using var negotiate = await client.PostAsync("/hubs/realtime/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode); // Control traffic still works at transport capacity.
        // Forged headers cannot evade the NAT realtime concurrency bound.
        using var overflow = new HttpRequestMessage(HttpMethod.Get, "/hubs/realtime");
        overflow.Headers.Add("X-Forwarded-For", "198.51.100.77");
        overflow.Headers.Add("CF-Connecting-IP", "192.0.2.77");
        using var rejected = await client.SendAsync(overflow);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
        var sockets = factory.Server.CreateWebSocketClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sockets.ConnectAsync(
            new Uri($"wss://localhost/hubs/realtime?access_token={a.Token}"), CancellationToken.None));

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await first.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "test cleanup", deadline.Token);
        await AssertRealtimeAvailableAsync(client, deadline.Token);
        using var replacement = await RealtimeApiTests.ConnectAsync(factory, a.Token);
        second.Socket.Abort();
        // Abort cleanup also returns a permit; no permanent occupancy leak.
        await AssertRealtimeAvailableAsync(client, deadline.Token);
    }

    [Fact]
    public async Task EachPoolStaysBoundedAndHealthSurvivesHttpAndRealtimeSaturation()
    {
        using var limiter = new PreAuthenticationRateLimiter(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimits:ConcurrentRequests"] = "1",
            ["RateLimits:Realtime:ConcurrentRequests"] = "2",
            ["RateLimits:Realtime:ConcurrentPerIp"] = "1",
            ["RateLimits:Health:ConcurrentRequests"] = "1",
            ["RateLimits:Health:ConcurrentPerIp"] = "1",
            ["RateLimits:Burst:PermitLimit"] = "3",
            ["RateLimits:Health:BurstPermitLimit"] = "3"
        }).Build());
        static DefaultHttpContext Context(string path, string ip = "203.0.113.11")
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = path;
            // Neither forwarded header is trusted by the partition function.
            context.Request.Headers["X-Forwarded-For"] = Guid.NewGuid().ToString();
            context.Request.Headers["CF-Connecting-IP"] = "198.51.100.77";
            return context;
        }
        using var realtime = await limiter.AcquireAsync(Context("/hubs/realtime"));
        using var otherRealtime = await limiter.AcquireAsync(Context("/hubs/realtime", "203.0.113.12"));
        using var globalRealtimeFull = await limiter.AcquireAsync(Context("/hubs/realtime", "203.0.113.13"));
        Assert.True(realtime.IsAcquired);
        Assert.True(otherRealtime.IsAcquired);
        Assert.False(globalRealtimeFull.IsAcquired);
        var sendContext = Context("/hubs/realtime");
        sendContext.Request.Method = HttpMethods.Post;
        using (var send = await limiter.AcquireAsync(sendContext)) Assert.True(send.IsAcquired);
        // POST/send consumed an IP burst permit but not a realtime slot.
        using var http = await limiter.AcquireAsync(Context("/api/v1/me", "203.0.113.15"));
        Assert.True(http.IsAcquired);
        using var httpFull = await limiter.AcquireAsync(Context("/api/v1/me"));
        Assert.False(httpFull.IsAcquired);
        var fakeHealth = Context("/api/v1/health", "203.0.113.16");
        fakeHealth.Request.Method = HttpMethods.Post;
        using var fakeHealthLease = await limiter.AcquireAsync(fakeHealth);
        Assert.False(fakeHealthLease.IsAcquired); // Only server-owned GET health routes get reserved capacity.
        http.Dispose();
        using var burstFull = await limiter.AcquireAsync(Context("/api/v1/me"));
        Assert.False(burstFull.IsAcquired); // Spoofing does not evade IP burst.
        using var health = await limiter.AcquireAsync(Context("/api/v1/health"));
        Assert.True(health.IsAcquired); // Reserved bounded pool, independent burst.
        using var healthFull = await limiter.AcquireAsync(Context("/health/live", "203.0.113.14"));
        Assert.False(healthFull.IsAcquired);
        health.Dispose();
        using var live = await limiter.AcquireAsync(Context("/health/live"));
        Assert.True(live.IsAcquired);
        live.Dispose();
        using var thirdHealth = await limiter.AcquireAsync(Context("/health/live"));
        Assert.True(thirdHealth.IsAcquired);
        thirdHealth.Dispose();
        using var healthBurstFull = await limiter.AcquireAsync(Context("/health/live"));
        Assert.False(healthBurstFull.IsAcquired); // Health is not an unbounded bypass.
    }

    [Theory]
    [InlineData("RateLimits:Realtime:ConcurrentRequests")]
    [InlineData("RateLimits:Realtime:ConcurrentPerIp")]
    [InlineData("RateLimits:Health:ConcurrentRequests")]
    [InlineData("RateLimits:Health:ConcurrentPerIp")]
    [InlineData("RateLimits:Health:BurstPermitLimit")]
    public void NewLimitsCannotDisableAdmission(string key)
    {
        // WebApplicationFactory applies overrides after Program service registration;
        // exercise the actual startup registration against its effective configuration.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = "0" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddHardening(configuration));
    }

    [Fact]
    public async Task MiddlewareCancellationAndFailureReturnOccupancyPermits()
    {
        using var limiter = new PreAuthenticationRateLimiter(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimits:Realtime:ConcurrentRequests"] = "1",
            ["RateLimits:Realtime:ConcurrentPerIp"] = "1"
        }).Build());
        var context = new DefaultHttpContext();
        context.Request.Path = "/hubs/realtime";
        using var cancel = new CancellationTokenSource();
        context.RequestAborted = cancel.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new PreAuthenticationRateLimitMiddleware(async http =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted);
        });
        var request = middleware.InvokeAsync(context, limiter);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => request));
        context.RequestAborted = CancellationToken.None;
        var failing = new PreAuthenticationRateLimitMiddleware(_ => throw new InvalidOperationException("Controlled transport failure"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.InvokeAsync(context, limiter));
        using var recovered = await limiter.AcquireAsync(context);
        Assert.True(recovered.IsAcquired);
    }

    [Fact]
    public async Task AuthenticatedLongPollHasSeparateBoundAndCancellationReleasesPermit()
    {
        using var factory = CreateFactory(false, realtimePerIp: 1);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RealtimeApiTests.RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        using var negotiated = await client.PostAsync("/hubs/realtime/negotiate?negotiateVersion=1", null);
        using var document = System.Text.Json.JsonDocument.Parse(await negotiated.Content.ReadAsStringAsync());
        var path = "/hubs/realtime?id=" + Uri.EscapeDataString(document.RootElement.GetProperty("connectionToken").GetString()!);
        using var initial = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        using var handshake = await client.PostAsync(path, new StringContent("{\"protocol\":\"json\",\"version\":1}\u001e", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.OK, handshake.StatusCode);
        using var acknowledgement = await client.GetAsync(path);
        Assert.Contains("{}", await acknowledgement.Content.ReadAsStringAsync());
        using var cancel = new CancellationTokenSource();
        using var pollRequest = new HttpRequestMessage(HttpMethod.Get, path);
        pollRequest.Headers.Add("Test-Pending-Poll", "true");
        var poll = client.SendAsync(pollRequest, cancel.Token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            // Read occupancy without taking a competing lease or sleeping for a
            // guessed duration. A real pending transport must hold the permit.
            var limiter = factory.Services.GetRequiredService<PreAuthenticationRateLimiter>();
            var probe = new DefaultHttpContext();
            probe.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.11");
            probe.Request.Method = HttpMethods.Get;
            probe.Request.Path = "/hubs/realtime";
            while (limiter.GetStatistics(probe)?.CurrentAvailablePermits != 0)
            {
                Assert.False(poll.IsCompleted);
                await Task.Delay(10, deadline.Token);
            }
            Assert.False(poll.IsCompleted);
            using var api = await client.GetAsync("/api/v1/me", deadline.Token);
            using var health = await client.GetAsync("/api/v1/health", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, api.StatusCode);
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
            using var overflow = await client.GetAsync(path, deadline.Token);
            Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
            using var ping = await client.PostAsync(path, new StringContent("{\"type\":6}\u001e", Encoding.UTF8, "text/plain"), deadline.Token);
            Assert.Equal(HttpStatusCode.OK, ping.StatusCode); // Send cannot be starved by the pending poll.
        }
        finally { cancel.Cancel(); }
        Assert.IsAssignableFrom<OperationCanceledException>(await Record.ExceptionAsync(() => poll));
        await AssertRealtimeAvailableAsync(client, deadline.Token);
        using var deleted = await client.DeleteAsync(path, deadline.Token);
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
    }

    [Fact]
    public async Task NatUsersRetainIndependentAuthenticatedRealtimePolicies()
    {
        using var factory = CreateFactory(false, realtimePerIp: 4, userPermit: 1);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var a = await RealtimeApiTests.RegisterAsync(client);
        var b = await RealtimeApiTests.RegisterAsync(client);
        using var first = await RealtimeApiTests.ConnectAsync(factory, a.Token);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", a.Token);
        using var denied = await client.PostAsync("/hubs/realtime/negotiate", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, denied.StatusCode);
        using var second = await RealtimeApiTests.ConnectAsync(factory, b.Token);
        Assert.Equal(WebSocketState.Open, second.Socket.State);
    }

    private static NexoraApiFactory CreateFactory(bool postgres, int realtimePerIp = 2, int userPermit = 30) =>
        new(null, new Dictionary<string, string?>
        {
            ["Realtime:Enabled"] = "true",
            ["RateLimits:ConcurrentRequests"] = "1",
            ["RateLimits:Realtime:ConcurrentRequests"] = "4",
            ["RateLimits:Realtime:ConcurrentPerIp"] = realtimePerIp.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["RateLimits:Realtime:PermitLimit"] = userPermit.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }, services => services.AddSingleton<IStartupFilter, NatPeerFilter>(),
            postgresConnectionString: postgres ? Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable) : null);

    private static async Task AssertRealtimeAvailableAsync(HttpClient client, CancellationToken token)
    {
        while (true)
        {
            using var response = await client.GetAsync("/hubs/realtime", token);
            // Real SignalR rejects missing connection ID only AFTER admission/auth.
            if (response.StatusCode == HttpStatusCode.BadRequest) return;
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            await Task.Delay(10, token);
        }
    }

    private sealed class NatPeerFilter : IStartupFilter
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
