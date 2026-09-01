using Nexora.Business.Practice;
using Nexora.Business.Privacy;

namespace Nexora.Worker;

public sealed partial class PracticeWorker(
    IServiceScopeFactory scopeFactory,
    AdaptivePollingBackoff pollingBackoff,
    ILogger<PracticeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>().ProcessPendingAsync(stoppingToken);
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
                pollingBackoff.Reset();
                await AdaptivePollingBackoff.DelayAsync(pollingBackoff.FailureDelay, stoppingToken);
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "Practice job polling failed")]
    private static partial void PollingFailed(ILogger logger, Exception exception);
}
