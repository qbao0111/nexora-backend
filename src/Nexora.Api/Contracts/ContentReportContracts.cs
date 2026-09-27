using System.ComponentModel.DataAnnotations;

namespace Nexora.Api.Contracts;

public sealed record SubmitContentReportRequest(
    [Required, StringLength(40)] string ContentType,
    [Required] Guid ContentId,
    [Required, StringLength(40)] string ReasonCode,
    [StringLength(1_000)] string? Description);

public sealed record ContentReportReceiptResponse(Guid ReportId, DateTimeOffset ReceivedAt);

public sealed record ContentReportAdminItemResponse(
    Guid Id,
    Guid ReporterUserId,
    string ContentType,
    Guid ContentId,
    string ReasonCode,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ModeratorUserId);

public sealed record ContentReportAdminDetailResponse(
    ContentReportAdminItemResponse Report,
    string? Description,
    string? ContentSnapshot,
    string? ResolutionCode,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAt);

public sealed record ContentReportAdminPageResponse(
    IReadOnlyCollection<ContentReportAdminItemResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record ResolveContentReportRequest(
    [Required, StringLength(20)] string Outcome,
    [Required, StringLength(40)] string ResolutionCode,
    [StringLength(1_000)] string? ResolutionNote);
