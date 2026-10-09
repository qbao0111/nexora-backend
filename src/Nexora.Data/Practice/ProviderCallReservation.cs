namespace Nexora.Data.Practice;

public sealed class ProviderCallReservation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid JobId { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string OperationKey { get; set; } = string.Empty;
    public int Attempt { get; set; }
    public long ReservedTokens { get; set; }
    public long? ActualPromptTokens { get; set; }
    public long? ActualCompletionTokens { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? FailureKind { get; set; }
    public DateTimeOffset? CooldownUntil { get; set; }
    // Private typed response checkpoint, never provider HTTP payload or API output.
    public string? ResultJson { get; set; }
    public string? ResultFingerprint { get; set; }
    public string? FailureRetryHint { get; set; }
}
