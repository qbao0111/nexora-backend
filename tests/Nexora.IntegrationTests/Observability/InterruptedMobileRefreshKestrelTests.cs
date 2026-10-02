using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Api.Observability;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests.Observability;

public sealed class InterruptedMobileRefreshKestrelTests
{
    [Fact]
    public async Task TruncatedContentLengthBodyDoesNotRotateSessionOrCaptureServerFailure()
    {
        var reporter = new RecordingReporter();
        var actions = new RefreshActionCounter();
        var completion = new InterruptedRequestCompletion();
        await using var factory = new NexoraApiFactory(new Dictionary<string, string?>(), services =>
        {
            services.RemoveAll<IApiSentryReporter>();
            services.AddSingleton<IApiSentryReporter>(reporter);
            services.Configure<MvcOptions>(options => options.Filters.Add(actions));
            services.AddSingleton<IStartupFilter>(completion);
        });
        factory.UseKestrel(0);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        client.BaseAddress = new Uri(Assert.Single(addresses.Addresses));
        factory.InitializeDatabase();
        var email = $"interrupted-refresh-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = "Strong!Pass123", displayName = "Test candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/mobile/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var payload = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var token = payload.RootElement.GetProperty("data").GetProperty("refreshToken").GetString()!;

        // Real TCP write-side EOF with a larger declared Content-Length. This is
        // not TestServer: Kestrel must reject the body before invoking the action.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(client.BaseAddress!.Host, client.BaseAddress.Port, timeout.Token);
        var body = JsonSerializer.Serialize(new { refreshToken = token });
        var request = $"POST /api/v1/auth/mobile/refresh HTTP/1.1\r\nHost: localhost\r\nX-Test-Interrupted: true\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body) + 20}\r\nConnection: close\r\n\r\n{body}";
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request), timeout.Token);
        tcp.Client.Shutdown(SocketShutdown.Send);
        using var reader = new StreamReader(stream);
        var response = await reader.ReadToEndAsync(timeout.Token);
        await completion.Finished.Task.WaitAsync(timeout.Token);
        // Kestrel may abort the response transport on EOF. An empty response is
        // expected on that path; when still writable it must be a client error.
        Assert.True(response.Length == 0 || response.StartsWith("HTTP/1.1 400", StringComparison.Ordinal));
        Assert.DoesNotContain("INTERNAL_ERROR", response, StringComparison.Ordinal);
        Assert.DoesNotContain("Unexpected end", response, StringComparison.Ordinal);
        Assert.DoesNotContain(token, response, StringComparison.Ordinal);
        Assert.Equal(0, reporter.CaptureCount);
        Assert.Equal(0, actions.InvocationCount);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var sessions = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().RefreshTokens.AsNoTracking().ToArrayAsync();
            var session = Assert.Single(sessions);
            Assert.Null(session.RevokedAt);
            Assert.Null(session.ReplacedByTokenHash);
        }

        using var refresh = await client.PostAsJsonAsync("/api/v1/auth/mobile/refresh", new { refreshToken = token });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal(1, actions.InvocationCount);
        using var refreshed = JsonDocument.Parse(await refresh.Content.ReadAsStringAsync());
        Assert.NotEqual(token, refreshed.RootElement.GetProperty("data").GetProperty("refreshToken").GetString());
        Assert.Equal(0, reporter.CaptureCount);
    }

    private sealed class RecordingReporter : IApiSentryReporter
    {
        private int _captureCount;
        public int CaptureCount => Volatile.Read(ref _captureCount);
        public void Capture(Exception exception, string requestId, string method, string path, int statusCode) =>
            Interlocked.Increment(ref _captureCount);
    }

    private sealed class RefreshActionCounter : IAsyncActionFilter
    {
        private int _invocationCount;
        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            if (context.ActionDescriptor is ControllerActionDescriptor { MethodInfo.Name: "MobileRefresh" })
                Interlocked.Increment(ref _invocationCount);
            await next();
        }
    }

    private sealed class InterruptedRequestCompletion : IStartupFilter
    {
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                try { await nextMiddleware(context); }
                finally
                {
                    if (context.Request.Headers.ContainsKey("X-Test-Interrupted"))
                        Finished.TrySetResult();
                }
            });
            next(app);
        };
    }
}
