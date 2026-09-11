using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Worker;
using Nexora.Worker.Observability;
using Sentry;

namespace Nexora.UnitTests.Observability;

public sealed class SentryObservabilityTests
{
    [Fact]
    public void ConfigurationDerivesEnvironmentAndReleaseAndDisablesPii()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sentry:Dsn"] = "https://public@example.invalid/1",
                ["Sentry:Release"] = "  build-2026.09.11  "
            })
            .Build();
        var environment = new TestHostEnvironment("Staging");
        var options = new Sentry.Extensions.Logging.SentryLoggingOptions();

        SentryObservability.Configure(options, environment, configuration);

        Assert.Equal("Staging", options.Environment);
        Assert.Equal("build-2026.09.11", options.Release);
        Assert.False(options.SendDefaultPii);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.None, options.MinimumEventLevel);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.None, options.MinimumBreadcrumbLevel);
        Assert.Null(options.TracesSampleRate);
        Assert.Null(options.ProfilesSampleRate);
    }

    [Fact]
    public void EmptyDsnUsesTheSdkDisableValue()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sentry:Dsn"] = "  "
            })
            .Build();
        var options = new Sentry.Extensions.Logging.SentryLoggingOptions();

        SentryObservability.Configure(options, new TestHostEnvironment("Development"), configuration);

        Assert.Equal(string.Empty, options.Dsn);
    }

    [Fact]
    public void SanitizerRemovesRequestSecretsAndSensitiveEventFields()
    {
        var sentryEvent = new SentryEvent(new InvalidOperationException("Bearer SUPER_SECRET_TOKEN"))
        {
            Request = new SentryRequest
            {
                Method = "POST",
                Url = "https://api.example.test/api/v1/interviews?access_token=SIGNALR_PRIVATE_TOKEN"
            }
        };
        sentryEvent.SetTag("request_id", "req-safe-1");
        sentryEvent.SetTag("secret", "refresh-secret-value");
        sentryEvent.SetExtra("body", "CV_PRIVATE_SENTENCE ANSWER_PRIVATE_SENTENCE");
        sentryEvent.AddBreadcrumb(new Breadcrumb("AI_PROMPT_PRIVATE_SENTENCE", "test", null, null, BreadcrumbLevel.Info));
        sentryEvent.SentryExceptions = [new Sentry.Protocol.SentryException
        {
            Type = "InvalidOperationException",
            Value = "raw answer"
        }];

        SentryObservability.Sanitize(sentryEvent);

        var json = Serialize(sentryEvent);
        Assert.Contains("/api/v1/interviews", json, StringComparison.Ordinal);
        Assert.Contains("request_id", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPER_SECRET_TOKEN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("refresh-secret-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SIGNALR_PRIVATE_TOKEN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CV_PRIVATE_SENTENCE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ANSWER_PRIVATE_SENTENCE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("AI_PROMPT_PRIVATE_SENTENCE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("access_token", json, StringComparison.Ordinal);
        Assert.DoesNotContain("?", sentryEvent.Request.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnexpectedPollingFailureIsReportedOnceAndWorkerCanStop()
    {
        var services = new ServiceCollection();
        services.AddScoped<IPrivacyJobProcessor, ThrowingPrivacyProcessor>();
        services.AddScoped<IPracticeJobProcessor, NoopPracticeProcessor>();
        services.AddScoped<IScenarioStarJobProcessor, NoopScenarioProcessor>();
        await using var provider = services.BuildServiceProvider();
        var reporter = new RecordingWorkerReporter();
        using var worker = new PracticeWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            CreateBackoff(failureDelayMilliseconds: 5_000),
            NullLogger<PracticeWorker>.Instance,
            reporter);

        await worker.StartAsync(CancellationToken.None);
        await reporter.Captured.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Single(reporter.Exceptions);
        Assert.NotEmpty(reporter.ExecutionIds.Single());
    }

    [Fact]
    public async Task ShutdownCancellationIsNotReported()
    {
        var services = new ServiceCollection();
        var blocking = new BlockingPrivacyProcessor();
        services.AddScoped<IPrivacyJobProcessor>(_ => blocking);
        services.AddScoped<IPracticeJobProcessor, NoopPracticeProcessor>();
        services.AddScoped<IScenarioStarJobProcessor, NoopScenarioProcessor>();
        await using var provider = services.BuildServiceProvider();
        var reporter = new RecordingWorkerReporter();
        using var worker = new PracticeWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            CreateBackoff(failureDelayMilliseconds: 1),
            NullLogger<PracticeWorker>.Instance,
            reporter);

        await worker.StartAsync(CancellationToken.None);
        await blocking.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await worker.StopAsync(CancellationToken.None);

        Assert.Empty(reporter.Exceptions);
    }

    private static AdaptivePollingBackoff CreateBackoff(int failureDelayMilliseconds) => new(new WorkerPollingOptions
    {
        BusyDelayMilliseconds = 0,
        IdleInitialDelayMilliseconds = 1,
        IdleMaximumDelayMilliseconds = 1,
        IdleBackoffMultiplier = 1,
        FailureDelayMilliseconds = failureDelayMilliseconds
    });

    private static string Serialize(SentryEvent sentryEvent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            sentryEvent.WriteTo(writer, null!);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private sealed class TestHostEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Nexora.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class RecordingWorkerReporter : IWorkerSentryReporter
    {
        public List<Exception> Exceptions { get; } = [];
        public List<string> ExecutionIds { get; } = [];
        public TaskCompletionSource Captured { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Capture(Exception exception, string executionId)
        {
            Exceptions.Add(exception);
            ExecutionIds.Add(executionId);
            Captured.TrySetResult();
        }
    }

    private sealed class ThrowingPrivacyProcessor : IPrivacyJobProcessor
    {
        public Task<int> ProcessPendingAsync(CancellationToken cancellationToken) =>
            Task.FromException<int>(new InvalidOperationException("worker failure"));
    }

    private sealed class BlockingPrivacyProcessor : IPrivacyJobProcessor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class NoopPracticeProcessor : IPracticeJobProcessor
    {
        public Task<int> ProcessPendingAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class NoopScenarioProcessor : IScenarioStarJobProcessor
    {
        public Task<int> ProcessPendingAsync(CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
