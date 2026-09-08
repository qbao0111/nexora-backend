namespace Nexora.Api.Realtime;

public sealed record ResourceChangedEvent(
    Guid EventId,
    string ResourceType,
    Guid ResourceId,
    string Status,
    DateTimeOffset OccurredAt)
{
    public const string Name = "resourceChanged";
}

public sealed class RealtimeOptions
{
    public const string SectionName = "Realtime";
    public bool Enabled { get; set; } = true;
    public int BatchSize { get; set; } = 50;
    public int BusyDelayMilliseconds { get; set; } = 200;
    public int IdleDelayMilliseconds { get; set; } = 1500;
    public int FailureDelayMilliseconds { get; set; } = 3000;
}
