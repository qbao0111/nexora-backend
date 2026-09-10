using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Api.Realtime;
using Nexora.Business.Ai;
using Nexora.Business.Practice;
using Nexora.Data.Persistence;
using Nexora.Data.Realtime;

namespace Nexora.IntegrationTests;

public sealed class RealtimeApiTests
{
    private static readonly string[] EventFields = ["eventId", "occurredAt", "resourceId", "resourceType", "status"];
    [Fact]
    public void UserIdUsesSubAndNeverClientNameIdentifier()
    {
        var transport = new DefaultConnectionContext();
        transport.Features.Set<IConnectionUserFeature>(new ConnectionUserFeature
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim("sub", "server-user"), new Claim(ClaimTypes.NameIdentifier, "other-user")], "test"))
        });
        var connection = new HubConnectionContext(transport, new HubConnectionContextOptions(), NullLoggerFactory.Instance);
        Assert.Equal("server-user", new SubClaimUserIdProvider().GetUserId(connection));
        Assert.Empty(typeof(RealtimeHub).GetMethods(System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly));
    }

    [Fact]
    public async Task UnauthenticatedClientCannotNegotiateOrConnect()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        using var response = await client.PostAsync("/hubs/realtime/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var sockets = factory.Server.CreateWebSocketClient();
        await Assert.ThrowsAsync<InvalidOperationException>(() => sockets.ConnectAsync(new Uri("wss://localhost/hubs/realtime"), CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidJwtConnectsThroughHeaderOrScopedQueryToken(bool queryToken)
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        using var socket = await ConnectAsync(factory, account.Token, queryToken);
        Assert.Equal(WebSocketState.Open, socket.Socket.State);
    }

    [Fact]
    public async Task QueryTokenIsIgnoredOutsideHubAndSecurityStampIsStillValidated()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        using var rest = await client.GetAsync($"/api/v1/me?access_token={account.Token}");
        Assert.Equal(HttpStatusCode.Unauthorized, rest.StatusCode);
        // Inspect the handler on a similarly prefixed path: no accidental global query authentication.
        var jwt = factory.Services.GetRequiredService<IOptionsMonitor<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>>().Get("Bearer");
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Request.Path = "/hubs/realtime-other";
        http.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?access_token={account.Token}");
        var received = new Microsoft.AspNetCore.Authentication.JwtBearer.MessageReceivedContext(http,
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme("Bearer", null, typeof(Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler)), jwt);
        await jwt.Events.OnMessageReceived(received);
        Assert.Null(received.Token);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        using var logout = await client.PostAsync("/api/v1/auth/logout-all", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        using var revoked = await client.PostAsync($"/hubs/realtime/negotiate?access_token={account.Token}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    [Theory]
    [InlineData("inactive")]
    [InlineData("deletionRequested")]
    [InlineData("deleted")]
    public async Task UnavailableUserCannotConnect(string state)
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var user = await db.Users.SingleAsync(item => item.Id == account.UserId);
            if (state == "inactive") user.IsActive = false;
            if (state == "deletionRequested") user.DeletionRequestedAt = DateTimeOffset.UtcNow;
            if (state == "deleted") user.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        using var response = await client.PostAsync($"/hubs/realtime/negotiate?access_token={account.Token}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task BroadcasterDeliversOnlyToOwnerWithAllowlistedPayloadAndStableDuplicateId()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var a = await RegisterAsync(client);
        var b = await RegisterAsync(client);
        using var socketA = await ConnectAsync(factory, a.Token);
        using var socketB = await ConnectAsync(factory, b.Token);
        // Send B first, then A as a barrier. If A were receiving broadcasts, its first event would be B's.
        var eventB = await EnqueueAsync(factory, b.UserId);
        using var broadcaster = CreateBroadcaster(factory);
        Assert.Equal(1, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        var eventA = await EnqueueAsync(factory, a.UserId);
        Assert.Equal(1, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        var payloadB = await socketB.ReadEventAsync();
        var payloadA = await socketA.ReadEventAsync();
        Assert.Equal(eventB, payloadB.GetProperty("eventId").GetGuid());
        Assert.Equal(eventA, payloadA.GetProperty("eventId").GetGuid());
        Assert.Equal(EventFields,
            payloadA.EnumerateObject().Select(item => item.Name).Order(StringComparer.Ordinal));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var row = await db.RealtimeNotifications.SingleAsync(item => item.Id == eventA);
            Assert.NotNull(row.ProcessedAt);
            row.ProcessedAt = null; // Simulate crash after send but before delivery acknowledgement was persisted.
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        Assert.Equal(eventA, (await socketA.ReadEventAsync()).GetProperty("eventId").GetGuid());
        Assert.Equal(0, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await finalDb.UsageEvents.ToArrayAsync());
        Assert.Empty(await finalDb.InterviewSessions.ToArrayAsync());
    }

    [Fact]
    public async Task FailedSendRemainsPendingRetriesLaterAndDoesNotBlockOtherRows()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        var failingId = await EnqueueAsync(factory, owner.UserId);
        var successfulId = await EnqueueAsync(factory, owner.UserId);
        var hub = new RecordingHubContext { FailingEventId = failingId };
        using var broadcaster = CreateBroadcaster(factory, hub);
        Assert.Equal(2, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var failed = await db.RealtimeNotifications.SingleAsync(item => item.Id == failingId);
            Assert.Null(failed.ProcessedAt);
            Assert.Equal(1, failed.Attempts);
            Assert.NotNull(failed.NextAttemptAt);
            Assert.NotNull((await db.RealtimeNotifications.SingleAsync(item => item.Id == successfulId)).ProcessedAt);
        }
        Assert.Equal(0, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        hub.FailingEventId = null;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var row = await db.RealtimeNotifications.SingleAsync(item => item.Id == failingId);
            row.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(1, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        Assert.All(hub.Sends, send => Assert.Equal(owner.UserId.ToString(), send.UserId));
        Assert.All(hub.Sends, send => Assert.Equal(ResourceChangedEvent.Name, send.Method));
        using var finalScope = factory.Services.CreateScope();
        Assert.NotNull((await finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>()
            .RealtimeNotifications.SingleAsync(item => item.Id == failingId)).ProcessedAt);
    }

    [Fact]
    public async Task DisabledRealtimeKeepsRestAndWorkerFunctional()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var hub = await client.PostAsync("/hubs/realtime/negotiate", null);
        Assert.Equal(HttpStatusCode.NotFound, hub.StatusCode);
        var session = await PostAsync(client, "/api/v1/interviews", InterviewInput());
        await ProcessJobsAsync(factory);
        using var resource = await client.GetAsync($"/api/v1/interviews/{session.GetProperty("id").GetGuid()}");
        Assert.Equal("active", (await DataAsync(resource)).GetProperty("status").GetString());
        using var broadcaster = new RealtimeNotificationBroadcaster(factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new RecordingHubContext(), Options.Create(new RealtimeOptions { Enabled = false }), TimeProvider.System,
            NullLogger<RealtimeNotificationBroadcaster>.Instance);
        Assert.Equal(0, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        using var scope = factory.Services.CreateScope();
        Assert.Null((await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().RealtimeNotifications.SingleAsync()).ProcessedAt);
    }

    [Fact]
    public async Task InterviewWorkerToHostedBroadcasterToRestWorksForActivationAndReport()
    {
        using var factory = CreateFactory(hosted: true);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var socket = await ConnectAsync(factory, owner.Token);
        var created = await PostAsync(client, "/api/v1/interviews", InterviewInput());
        var id = created.GetProperty("id").GetGuid();
        Assert.Equal("starting", created.GetProperty("status").GetString());
        await ProcessJobsAsync(factory);
        Assert.Equal("active", (await socket.ReadEventAsync()).GetProperty("status").GetString());
        using var activeResponse = await client.GetAsync($"/api/v1/interviews/{id}");
        var active = await DataAsync(activeResponse);
        var answer = await PostAsync(client, $"/api/v1/interviews/{id}/answers",
            new { questionId = active.GetProperty("questions")[0].GetProperty("id").GetGuid(), content = "Dependency injection supplies dependencies through constructors." });
        var secondAnswer = await PostAsync(client, $"/api/v1/interviews/{id}/answers",
            new { questionId = answer.GetProperty("nextQuestion").GetProperty("id").GetGuid(), content = "I use a scoped lifetime for the database context." });
        await PostAsync(client, $"/api/v1/interviews/{id}/answers",
            new { questionId = secondAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid(), content = "The role aligns with my experience." });
        var completing = await PostAsync(client, $"/api/v1/interviews/{id}/complete", null);
        Assert.Equal("completing", completing.GetProperty("status").GetString());
        await ProcessJobsAsync(factory);
        var completed = await socket.ReadEventAsync();
        Assert.Equal("completed", completed.GetProperty("status").GetString());
        Assert.Equal(id, completed.GetProperty("resourceId").GetGuid());
        using var report = await client.GetAsync($"/api/v1/interviews/{id}/report");
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(2, await db.RealtimeNotifications.CountAsync());
        Assert.Equal(3, await db.InterviewQuestions.CountAsync());
        Assert.Equal(1, await db.InterviewReports.CountAsync());
    }

    internal static NexoraApiFactory CreateFactory(bool hosted = false, Action<IServiceCollection>? configure = null) =>
        new(new Dictionary<string, string?> { ["Realtime:Enabled"] = "true", ["Realtime:IdleDelayMilliseconds"] = "25" }, services =>
        {
            if (!hosted)
            {
                var registration = services.Single(item => item.ServiceType == typeof(IHostedService) &&
                    item.ImplementationType == typeof(RealtimeNotificationBroadcaster));
                services.Remove(registration);
            }
            configure?.Invoke(services);
        });

    internal static RealtimeNotificationBroadcaster CreateBroadcaster(NexoraApiFactory factory, IHubContext<RealtimeHub>? hub = null) =>
        new(factory.Services.GetRequiredService<IServiceScopeFactory>(), hub ?? factory.Services.GetRequiredService<IHubContext<RealtimeHub>>(),
            Options.Create(new RealtimeOptions()), TimeProvider.System, NullLogger<RealtimeNotificationBroadcaster>.Instance);

    private static async Task<Guid> EnqueueAsync(NexoraApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var row = new RealtimeNotification { UserId = userId, ResourceType = "interview", ResourceId = Guid.NewGuid(), Status = "active", CreatedAt = DateTimeOffset.UtcNow };
        db.RealtimeNotifications.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    internal static async Task<(Guid UserId, string Token)> RegisterAsync(HttpClient client)
    {
        var email = $"realtime-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        { email, password = "Strong!Pass123", displayName = "Synthetic candidate" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return (data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    internal static object InterviewInput() => new { role = "Backend developer", seniority = "junior", interviewType = "technical", difficulty = "medium" };

    internal static async Task<JsonElement> PostAsync(HttpClient client, string path, object? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await DataAsync(response);
    }

    internal static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    internal static async Task ProcessJobsAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>().ProcessPendingAsync(CancellationToken.None);
    }

    internal static async Task<HubSocket> ConnectAsync(NexoraApiFactory factory, string token, bool queryToken = true)
    {
        var sockets = factory.Server.CreateWebSocketClient();
        if (!queryToken) sockets.ConfigureRequest = request => request.Headers.Authorization = $"Bearer {token}";
        var uri = new Uri($"wss://localhost/hubs/realtime{(queryToken ? $"?access_token={token}" : string.Empty)}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var socket = new HubSocket(await sockets.ConnectAsync(uri, timeout.Token));
        await socket.Socket.SendAsync(Encoding.UTF8.GetBytes("{\"protocol\":\"json\",\"version\":1}\u001e").AsMemory(),
            WebSocketMessageType.Text, true, timeout.Token);
        var handshake = await socket.ReadMessageAsync();
        Assert.False(handshake.TryGetProperty("error", out _));
        return socket;
    }

    internal sealed class HubSocket(WebSocket socket) : IDisposable
    {
        public WebSocket Socket { get; } = socket;
        private string _pending = string.Empty;
        public async Task<JsonElement> ReadMessageAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var buffer = new byte[8192];
            while (!_pending.Contains('\u001e', StringComparison.Ordinal))
            {
                var result = await Socket.ReceiveAsync(buffer.AsMemory(), timeout.Token);
                Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
                _pending += Encoding.UTF8.GetString(buffer, 0, result.Count);
            }
            var end = _pending.IndexOf('\u001e', StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(_pending[..end]);
            _pending = _pending[(end + 1)..];
            return doc.RootElement.Clone();
        }
        public async Task<JsonElement> ReadEventAsync()
        {
            var message = await ReadMessageAsync();
            Assert.Equal(1, message.GetProperty("type").GetInt32());
            Assert.Equal(ResourceChangedEvent.Name, message.GetProperty("target").GetString());
            return message.GetProperty("arguments")[0].Clone();
        }
        public void Dispose() { Socket.Abort(); Socket.Dispose(); }
    }

    private sealed class ConnectionUserFeature : IConnectionUserFeature
    {
        public ClaimsPrincipal? User { get; set; }
    }

    private sealed class RecordingHubContext : IHubContext<RealtimeHub>, IHubClients
    {
        public Guid? FailingEventId { get; set; }
        public List<(string UserId, string Method)> Sends { get; } = [];
        public IHubClients Clients => this;
        public IGroupManager Groups => throw new NotSupportedException();
        public IClientProxy User(string userId) => new RecordingProxy(this, userId);
        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        IClientProxy IHubClients<IClientProxy>.Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy Group(string groupName) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        IClientProxy IHubClients<IClientProxy>.Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
        private sealed class RecordingProxy(RecordingHubContext owner, string userId) : IClientProxy
        {
            public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
            {
                owner.Sends.Add((userId, method));
                return ((ResourceChangedEvent)args[0]!).EventId == owner.FailingEventId
                    ? Task.FromException(new InvalidOperationException("Synthetic transport failure")) : Task.CompletedTask;
            }
        }
    }
}
