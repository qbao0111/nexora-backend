using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Data.Persistence;
using Nexora.Worker.Observability;

namespace Nexora.Worker;

public sealed partial class PracticeWorker(
    IServiceScopeFactory scopeFactory,
    AdaptivePollingBackoff pollingBackoff,
    ILogger<PracticeWorker> logger,
    IWorkerSentryReporter sentryReporter,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var databaseBackoff = new DatabaseOutageBackoff(TimeProvider.System);
        while (!stoppingToken.IsCancellationRequested)
        {
            var workerCycleId = Guid.NewGuid().ToString("N");
            try
            {
                using var scope = scopeFactory.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>().ProcessPendingAsync(stoppingToken);
                // Pause queued AI work without consuming/retrying/failing its jobs.
                // Privacy processing remains independent of the AI kill switch.
                if (configuration.GetValue("Features:Ai", true))
                {
                    count += await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>().ProcessPendingAsync(stoppingToken);
                    count += await scope.ServiceProvider.GetRequiredService<IScenarioStarJobProcessor>().ProcessPendingAsync(stoppingToken);
                }
                databaseBackoff.Reset();
                if (count > 0)
                {
                    pollingBackoff.Reset();
                    await AdaptivePollingBackoff.DelayAsync(pollingBackoff.BusyDelay, stoppingToken);
                }
                else
                {
                    await AdaptivePollingBackoff.DelayAsync(pollingBackoff.NextIdleDelay(), stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception) when (DatabaseConnectivityFailure.IsTransient(exception))
            {
                var (delay, report) = databaseBackoff.Next(Random.Shared.NextDouble());
                if (report)
                {
                    DatabaseUnavailable(logger);
                    sentryReporter.Capture(exception, workerCycleId);
                }
                // New scope next cycle; no transaction/provider side-effect replay.
                try { await AdaptivePollingBackoff.DelayAsync(delay, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            }
            catch (Exception exception)
            {
                PollingFailed(logger, exception.GetType().Name);
                sentryReporter.Capture(exception, workerCycleId);
                pollingBackoff.Reset();
                await AdaptivePollingBackoff.DelayAsync(pollingBackoff.FailureDelay, stoppingToken);
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "Practice job polling failed with {ExceptionType}")]
    private static partial void PollingFailed(ILogger logger, string exceptionType);

    [LoggerMessage(LogLevel.Warning, "Worker database temporarily unavailable; bounded recovery polling scheduled")]
    private static partial void DatabaseUnavailable(ILogger logger);
}
