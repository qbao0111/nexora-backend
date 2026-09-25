using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Worker.Observability;

namespace Nexora.Worker;

public sealed partial class PracticeWorker(
    IServiceScopeFactory scopeFactory,
    AdaptivePollingBackoff pollingBackoff,
    ILogger<PracticeWorker> logger,
    IWorkerSentryReporter sentryReporter) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var workerCycleId = Guid.NewGuid().ToString("N");
            try
            {
                using var scope = scopeFactory.CreateScope();
                var count = 0;
                var billingService = scope.ServiceProvider.GetService<IBillingService>();
                if (billingService is not null)
                    count += await billingService.ExpirePendingPaymentsAsync(stoppingToken);
                count += await scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>().ProcessPendingAsync(stoppingToken);
                count += await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>().ProcessPendingAsync(stoppingToken);
                count += await scope.ServiceProvider.GetRequiredService<IScenarioStarJobProcessor>().ProcessPendingAsync(stoppingToken);
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
            catch (Exception exception)
            {
                PollingFailed(logger, exception);
                sentryReporter.Capture(exception, workerCycleId);
                pollingBackoff.Reset();
                await AdaptivePollingBackoff.DelayAsync(pollingBackoff.FailureDelay, stoppingToken);
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "Practice job polling failed")]
    private static partial void PollingFailed(ILogger logger, Exception exception);
}
