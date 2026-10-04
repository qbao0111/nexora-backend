using Nexora.Business.Privacy;

namespace Nexora.Worker;

public sealed partial class RetentionWorker(
    IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Independent of the short practice poll. Durable DB schedule decides
        // whether a sweep is due; all replicas use the same transaction lock.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IRetentionProcessor>().RunDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Connection/startup failures cannot save a checkpoint. Retry
                // no sooner than the next timer tick, without logging payloads.
                PollFailed(logger, exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromMinutes(5), timeProvider, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(LogLevel.Warning, "Retention polling failed with {ExceptionType}")]
    private static partial void PollFailed(ILogger logger, string exceptionType);
}
