namespace Nexora.Worker;

public sealed partial class FoundationWorker(ILogger<FoundationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        WorkerStarted(logger);
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }

    [LoggerMessage(LogLevel.Information, "Nexora worker foundation started")]
    private static partial void WorkerStarted(ILogger logger);
}
