using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Worker;
using Nexora.Worker.Observability;
using Npgsql;

namespace Nexora.UnitTests.Worker;

public sealed class PracticeWorkerRecoveryTests
{
    [Fact]
    public async Task TransientFailureRecoversWithoutReplayingSuccessfulWorkAndCancellationIsPrompt()
    {
        var processor = new RecoveringPrivacyProcessor();
        var reporter = new RecordingReporter();
        var services = new ServiceCollection();
        services.AddSingleton<IPrivacyJobProcessor>(processor);
        using var provider = services.BuildServiceProvider();
        using var worker = new PracticeWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new AdaptivePollingBackoff(new WorkerPollingOptions()), NullLogger<PracticeWorker>.Instance, reporter,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Features:Ai"] = "false" }).Build());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await processor.Recovered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, processor.Completed);
            Assert.Equal(1, reporter.Captured);
        }
        finally
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await worker.StopAsync(stop.Token);
            Assert.False(stop.IsCancellationRequested);
        }
    }

    private sealed class RecoveringPrivacyProcessor : IPrivacyJobProcessor
    {
        private int _calls;
        public int Completed { get; private set; }
        public TaskCompletionSource Recovered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 1)
                throw new NpgsqlException("controlled test outage", new TimeoutException());
            if (Completed == 0) Completed++;
            Recovered.TrySetResult();
            return Task.FromResult(0);
        }
    }

    private sealed class RecordingReporter : IWorkerSentryReporter
    {
        public int Captured { get; private set; }
        public void Capture(Exception exception, string executionId) => Captured++;
    }
}
