using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Api.Infrastructure;
using Nexora.Api.Observability;
using Nexora.Business.Common;
using Sentry.AspNetCore;
using Sentry.Extensibility;

namespace Nexora.IntegrationTests.Observability;

public sealed class SentryApiObservabilityTests
{
    [Fact]
    public async Task UnexpectedExceptionIsCapturedOnceAndReturnsSafeEnvelope()
    {
        var reporter = new RecordingApiReporter();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new InvalidOperationException("Bearer SUPER_SECRET_TOKEN"),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            reporter);
        var context = CreateContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.Single(reporter.Captures);
        var capture = reporter.Captures[0];
        Assert.Equal("req-api-123", capture.RequestId);
        Assert.Equal("POST", capture.Method);
        Assert.Equal("/api/v1/interviews", capture.Path);
        Assert.Equal(StatusCodes.Status500InternalServerError, capture.StatusCode);
        var response = await ReadResponseAsync(context);
        Assert.Contains("INTERNAL_ERROR", response, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPER_SECRET_TOKEN", response, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BusinessErrorKind.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(BusinessErrorKind.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(BusinessErrorKind.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(BusinessErrorKind.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(BusinessErrorKind.Conflict, StatusCodes.Status409Conflict)]
    public async Task ExpectedBusinessFailuresAreNotCaptured(BusinessErrorKind kind, int expectedStatus)
    {
        var reporter = new RecordingApiReporter();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new BusinessException("EXPECTED_ERROR", "Dữ liệu không hợp lệ.", kind),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            reporter);
        var context = CreateContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
        Assert.Empty(reporter.Captures);
        var response = await ReadResponseAsync(context);
        Assert.Contains("EXPECTED_ERROR", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExternalFailureIsCapturedOnceAndKeepsSafeEnvelope()
    {
        var reporter = new RecordingApiReporter();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new BusinessException(
                "AI_PROVIDER_UNAVAILABLE",
                "Dịch vụ AI tạm thời không khả dụng.",
                BusinessErrorKind.ExternalFailure),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            reporter);
        var context = CreateContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        var capture = Assert.Single(reporter.Captures);
        Assert.Equal("req-api-123", capture.RequestId);
        Assert.Equal("POST", capture.Method);
        Assert.Equal("/api/v1/interviews", capture.Path);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, capture.StatusCode);
        var response = await ReadResponseAsync(context);
        Assert.Contains("AI_PROVIDER_UNAVAILABLE", response, StringComparison.Ordinal);
        Assert.Contains("Dịch vụ AI tạm thời không khả dụng.", response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequestAbortedCancellationIsNotCaptured()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var reporter = new RecordingApiReporter();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new OperationCanceledException(cancellation.Token),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            reporter);
        var context = CreateContext();
        context.RequestAborted = cancellation.Token;

        await middleware.InvokeAsync(context);

        Assert.Empty(reporter.Captures);
    }

    [Fact]
    public void ConfigurationUsesHostEnvironmentAndEnforcesPrivacyDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sentry:Dsn"] = "https://public@example.invalid/1",
                ["Sentry:Release"] = "api-build-42"
            })
            .Build();
        var options = new SentryAspNetCoreOptions();

        SentryObservability.Configure(options, new TestWebHostEnvironment("Production"), configuration);

        Assert.Equal("Production", options.Environment);
        Assert.Equal("api-build-42", options.Release);
        Assert.False(options.SendDefaultPii);
        Assert.Equal(RequestSize.None, options.MaxRequestBodySize);
        Assert.False(options.IncludeActivityData);
        Assert.False(options.AutoRegisterTracing);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.None, options.MinimumEventLevel);
        Assert.Equal("api", options.DefaultTags["service"]);
    }

    [Fact]
    public void ExplicitApiCaptureSetsTheSafeServiceTag()
    {
        var hub = new RecordingHub();

        new ApiSentryReporter(hub).Capture(
            new InvalidOperationException("unexpected"),
            "req-api-123",
            "GET",
            "/api/v1/health/live",
            StatusCodes.Status500InternalServerError);

        var sentryEvent = Assert.Single(hub.Events);
        Assert.Equal("api", sentryEvent.Tags["service"]);
        Assert.Equal("req-api-123", sentryEvent.Tags["request_id"]);
    }

    [Fact]
    public void SanitizerKeepsSafeRouteButDropsQueryAndSensitiveValues()
    {
        var sentryEvent = new Sentry.SentryEvent(new InvalidOperationException("CV_PRIVATE_SENTENCE"))
        {
            Request = new Sentry.SentryRequest
            {
                Method = "POST",
                Url = "https://api.example.test/api/v1/interviews?access_token=SIGNALR_PRIVATE_TOKEN"
            }
        };
        sentryEvent.SetTag("service", "api");
        sentryEvent.SetTag("answer", "ANSWER_PRIVATE_SENTENCE");
        sentryEvent.SetExtra("prompt", "AI_PROMPT_PRIVATE_SENTENCE");

        var sanitized = SentryObservability.Sanitize(sentryEvent);

        Assert.Equal("/api/v1/interviews", sanitized.Request.Url);
        Assert.Equal("POST", sanitized.Request.Method);
        Assert.Contains("service", sanitized.Tags.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("answer", sanitized.Tags.Keys, StringComparer.Ordinal);
        Assert.Empty(sanitized.Extra);
        Assert.DoesNotContain("?", sanitized.Request.Url, StringComparison.Ordinal);
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "req-api-123";
        context.Request.Method = "POST";
        context.Request.Path = "/api/v1/interviews";
        context.Request.QueryString = new QueryString("?access_token=SIGNALR_PRIVATE_TOKEN");
        context.Request.Headers.Authorization = "Bearer SUPER_SECRET_TOKEN";
        context.Request.Headers.Cookie = "refresh-token=refresh-secret-value";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("ANSWER_PRIVATE_SENTENCE"));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<string> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync();
    }

    private sealed class RecordingApiReporter : IApiSentryReporter
    {
        public List<Capture> Captures { get; } = [];

        public void Capture(Exception exception, string requestId, string method, string path, int statusCode) =>
            Captures.Add(new Capture(exception, requestId, method, path, statusCode));
    }

    private sealed record Capture(Exception Exception, string RequestId, string Method, string Path, int StatusCode);

    private sealed class RecordingHub : IHub
    {
        public List<Sentry.SentryEvent> Events { get; } = [];
        public bool IsEnabled => true;
        public Sentry.SentryId LastEventId => default;
        public Sentry.SentryStructuredLogger Logger => null!;
        public Sentry.SentryMetricEmitter Metrics => null!;
        public bool IsSessionActive => false;

        public Sentry.SentryId CaptureEvent(Sentry.SentryEvent sentryEvent, Action<Sentry.Scope> configureScope)
        {
            var scope = new Sentry.Scope(new Sentry.SentryOptions());
            configureScope(scope);
            scope.Apply(sentryEvent);
            Events.Add(sentryEvent);
            return sentryEvent.EventId;
        }

        public Sentry.SentryId CaptureEvent(Sentry.SentryEvent sentryEvent, Sentry.SentryHint? hint, Action<Sentry.Scope> configureScope) =>
            CaptureEvent(sentryEvent, configureScope);

        public Sentry.SentryId CaptureEvent(Sentry.SentryEvent evt, Sentry.Scope? scope, Sentry.SentryHint? hint) => CaptureEvent(evt, _ => { });
        public Sentry.SentryId CaptureFeedback(Sentry.SentryFeedback feedback, out Sentry.CaptureFeedbackResult result, Sentry.Scope? scope, Sentry.SentryHint? hint)
        {
            result = default!;
            return default;
        }
        public void CaptureTransaction(Sentry.SentryTransaction transaction) { }
        public void CaptureTransaction(Sentry.SentryTransaction transaction, Sentry.Scope? scope, Sentry.SentryHint? hint) { }
        public void CaptureSession(Sentry.SessionUpdate sessionUpdate) { }
        public Sentry.SentryId CaptureCheckIn(string monitorSlug, Sentry.CheckInStatus status, Sentry.SentryId? sentryId, TimeSpan? duration, Sentry.Scope? scope, Action<Sentry.SentryMonitorOptions>? configureMonitorOptions) => default;
        public bool CaptureEnvelope(Sentry.Protocol.Envelopes.Envelope envelope) => false;
        public Task FlushAsync(TimeSpan timeout) => Task.CompletedTask;
        public void ConfigureScope(Action<Sentry.Scope> configureScope) { }
        public void ConfigureScope<TArg>(Action<Sentry.Scope, TArg> configureScope, TArg arg) { }
        public Task ConfigureScopeAsync(Func<Sentry.Scope, Task> configureScope) => Task.CompletedTask;
        public Task ConfigureScopeAsync<TArg>(Func<Sentry.Scope, TArg, Task> configureScope, TArg arg) => Task.CompletedTask;
        public void SetTag(string key, string value) { }
        public void UnsetTag(string key) { }
        public void BindClient(Sentry.ISentryClient client) { }
        public IDisposable PushScope() => null!;
        public IDisposable PushScope<TState>(TState state) => null!;

        public Sentry.ITransactionTracer StartTransaction(Sentry.ITransactionContext context, IReadOnlyDictionary<string, object?> customSamplingContext) => null!;
        public void BindException(Exception exception, Sentry.ISpan span) { }
        public Sentry.ISpan GetSpan() => null!;
        public Sentry.SentryTraceHeader GetTraceHeader() => null!;
        public Sentry.BaggageHeader GetBaggage() => null!;
        public Sentry.W3CTraceparentHeader GetTraceparentHeader() => null!;
        public Sentry.TransactionContext ContinueTrace(string? traceHeader, string? baggageHeader, string? name = null, string? operation = null) => null!;
        public Sentry.TransactionContext ContinueTrace(Sentry.SentryTraceHeader? traceHeader, Sentry.BaggageHeader? baggageHeader, string? name = null, string? operation = null) => null!;
        public void StartSession() { }
        public void PauseSession() { }
        public void ResumeSession() { }
        public void EndSession(Sentry.SessionEndStatus status) { }
        public Sentry.SentryId CaptureFeedback(Sentry.SentryFeedback feedback, out Sentry.CaptureFeedbackResult result, Action<Sentry.Scope> configureScope, Sentry.SentryHint? hint)
        {
            result = default!;
            return default;
        }
    }

    private sealed class TestWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Nexora.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
