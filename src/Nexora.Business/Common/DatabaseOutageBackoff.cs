namespace Nexora.Business.Common;

public sealed class DatabaseOutageBackoff(TimeProvider timeProvider)
{
    private int _failures;
    private DateTimeOffset? _lastReport;

    public (TimeSpan Delay, bool Report) Next(double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(jitter, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(jitter, 1);
        var now = timeProvider.GetUtcNow();
        var report = _lastReport is null || now - _lastReport >= TimeSpan.FromMinutes(5);
        if (report) _lastReport = now;
        var ceiling = Math.Min(60, 5 * Math.Pow(2, Math.Min(_failures++, 4)));
        // Equal jitter: 30-60 seconds at the cap, replicas desynchronised.
        // First wait is >= 5 seconds; no busy connection retry loop.
        return (TimeSpan.FromSeconds(Math.Max(5, ceiling * (0.5 + jitter / 2))), report);
    }

    public void Reset()
    {
        _failures = 0;
        _lastReport = null;
    }
}
