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

    [Fact]
    public async Task ExpectedBusinessFailureIsNotCaptured()
    {
        var reporter = new RecordingApiReporter();
        var middleware = new ExceptionHandlingMiddleware(
            _ => throw new BusinessException("VALIDATION_ERROR", "Dữ liệu không hợp lệ.", BusinessErrorKind.Validation),
            NullLogger<ExceptionHandlingMiddleware>.Instance,
            reporter);
        var context = CreateContext();

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.Empty(reporter.Captures);
        var response = await ReadResponseAsync(context);
        Assert.Contains("VALIDATION_ERROR", response, StringComparison.Ordinal);
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
