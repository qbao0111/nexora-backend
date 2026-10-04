namespace Nexora.Data.Privacy;

// One durable scheduler row; PostgreSQL transaction advisory lock owns each run.
public sealed class RetentionCheckpoint
{
    public int Id { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset? LastRunAt { get; set; }
    public string Status { get; set; } = "not_run";
    public int ConsecutiveFailures { get; set; }
    public bool DryRun { get; set; } = true;
    public int Examined { get; set; }
    public int Eligible { get; set; }
    public int Removed { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
}

// Opaque user ID or null for a global hold. No personal text/case narrative.
// Operator inserts/releases under the same advisory lock as the processor.
public sealed class RetentionHold
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
}
