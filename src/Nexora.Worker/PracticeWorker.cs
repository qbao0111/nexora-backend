using Nexora.Business.Practice;

namespace Nexora.Worker;

public sealed partial class PracticeWorker(IServiceScopeFactory scopeFactory, ILogger<PracticeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var count = await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>().ProcessPendingAsync(stoppingToken);
                if (count == 0) await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                PollingFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    [LoggerMessage(LogLevel.Error, "Practice job polling failed")]
    private static partial void PollingFailed(ILogger logger, Exception exception);
}
