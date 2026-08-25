using Nexora.Worker;

namespace Nexora.UnitTests.Worker;

public sealed class AdaptivePollingBackoffTests
{
    [Fact]
    public void IdleDelayProgressesToConfiguredCapAndStaysBounded()
    {
        var backoff = CreateBackoff();

        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.NextIdleDelay());
        Assert.Equal(TimeSpan.FromMilliseconds(200), backoff.NextIdleDelay());
        Assert.Equal(TimeSpan.FromMilliseconds(400), backoff.NextIdleDelay());
        Assert.Equal(TimeSpan.FromMilliseconds(500), backoff.NextIdleDelay());
        Assert.Equal(TimeSpan.FromMilliseconds(500), backoff.NextIdleDelay());
    }

    [Fact]
    public void ResetReturnsIdleDelayToInitialValue()
    {
        var backoff = CreateBackoff();
        _ = backoff.NextIdleDelay();
        _ = backoff.NextIdleDelay();

        backoff.Reset();

        Assert.Equal(TimeSpan.FromMilliseconds(100), backoff.NextIdleDelay());
    }

    [Fact]
    public async Task DelayHonorsAnAlreadyCancelledToken()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AdaptivePollingBackoff.DelayAsync(TimeSpan.FromMinutes(1), cancellation.Token));
    }

    private static AdaptivePollingBackoff CreateBackoff() => new(new WorkerPollingOptions
    {
        BusyDelayMilliseconds = 0,
        IdleInitialDelayMilliseconds = 100,
        IdleMaximumDelayMilliseconds = 500,
        IdleBackoffMultiplier = 2,
        FailureDelayMilliseconds = 1_000
    });
}
