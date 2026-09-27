using Nexora.Data.Identity;

namespace Nexora.Data.ContentReports;

public sealed class ContentReport
{
    public Guid Id { get; set; }
    public Guid ReporterUserId { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public Guid ContentId { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? ContentSnapshot { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public Guid? ModeratorUserId { get; set; }
    public string? ResolutionCode { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public int Version { get; set; }
    public ApplicationUser Reporter { get; set; } = null!;
    public ApplicationUser? Moderator { get; set; }
}
