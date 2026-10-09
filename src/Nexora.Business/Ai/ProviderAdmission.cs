namespace Nexora.Business.Ai;

public sealed record ProviderAdmissionRequest(
    Guid UserId, Guid JobId, string Purpose, string OperationKey, int Attempt, long ReservedTokens);

public interface IProviderAdmission
{
    Task<Guid> ReserveAsync(ProviderAdmissionRequest request, CancellationToken cancellationToken);
    Task CompleteAsync(Guid reservationId, AiTokenUsage? usage, CancellationToken cancellationToken, AiProviderFailureKind? failureKind = null);
}

public sealed record AiTokenUsage(long PromptTokens, long CompletionTokens);

public sealed class ProviderBudgetOptions
{
    public const string SectionName = "Ai:Budget";
    public int UserHourlyCalls { get; set; } = 60;
    public int UserDailyCalls { get; set; } = 200;
    public int GlobalDailyCalls { get; set; } = 2_000;
    public long UserHourlyTokens { get; set; } = 500_000;
    public long UserDailyTokens { get; set; } = 1_000_000;
    public long GlobalDailyTokens { get; set; } = 50_000_000;
    public int GlobalInFlight { get; set; } = 4;
    public int MaximumInputBytes { get; set; } = 256_000;
    public int MaximumQueuedJobs { get; set; } = 500;
    public int CooldownSeconds { get; set; } = 30;
    public int CooldownFailureThreshold { get; set; } = 3;
    public int MaximumSpeechSessions { get; set; } = 2;
    public Dictionary<string, int> PurposeHourlyCalls { get; set; } = new(StringComparer.Ordinal);
}
