namespace Nexora.Business.Privacy;

public sealed class RetentionOptions
{
    public const string SectionName = "Privacy:Retention";
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public bool PurgeEnabled { get; set; }
    public int SweepIntervalHours { get; set; } = 6;
    public int BatchSize { get; set; } = 100;
    public int VerificationGraceHours { get; set; }
    public int AuditRetentionMonths { get; set; } = 12;
    public int FailureBackoffMinutes { get; set; } = 30;
    public int MaxConsecutiveFailures { get; set; } = 5;
}

// Counts describe bounded samples, not the total database backlog.
public sealed record RetentionRunResult(
    string Status, bool DryRun, int Examined = 0, int Eligible = 0,
    int Removed = 0, int Skipped = 0, int Failed = 0);

public interface IRetentionProcessor
{
    Task<RetentionRunResult> RunDueAsync(CancellationToken cancellationToken);
}
