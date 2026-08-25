namespace Nexora.Worker;

public sealed class WorkerPollingOptions
{
    public const string SectionName = "Worker:Polling";

    public int BusyDelayMilliseconds { get; init; } = 25;
    public int IdleInitialDelayMilliseconds { get; init; } = 250;
    public int IdleMaximumDelayMilliseconds { get; init; } = 5_000;
    public double IdleBackoffMultiplier { get; init; } = 2;
    public int FailureDelayMilliseconds { get; init; } = 5_000;
}

public sealed class AdaptivePollingBackoff(WorkerPollingOptions options)
{
    private int _idleAttempt;

    public TimeSpan BusyDelay => TimeSpan.FromMilliseconds(options.BusyDelayMilliseconds);
    public TimeSpan FailureDelay => TimeSpan.FromMilliseconds(options.FailureDelayMilliseconds);

    public TimeSpan NextIdleDelay()
    {
        var multiplier = Math.Pow(options.IdleBackoffMultiplier, _idleAttempt++);
        var milliseconds = Math.Min(
            options.IdleMaximumDelayMilliseconds,
            options.IdleInitialDelayMilliseconds * multiplier);
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    public void Reset() => _idleAttempt = 0;

    public static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}
