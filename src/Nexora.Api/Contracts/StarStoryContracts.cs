using System.ComponentModel.DataAnnotations;

namespace Nexora.Api.Contracts;

public sealed class StarStoryCreateRequest
{
    [Required] public Guid SourceAttemptId { get; init; }
    [MaxLength(160)] public string? Title { get; init; }
    [MaxLength(8)] public string[] Tags { get; init; } = [];
}

public sealed class StarStoryUpdateRequest
{
    [Required, MaxLength(160)] public string Title { get; init; } = string.Empty;
    [MaxLength(8)] public string[] Tags { get; init; } = [];
    [Required, MaxLength(8_000)] public string Situation { get; init; } = string.Empty;
    [Required, MaxLength(8_000)] public string Task { get; init; } = string.Empty;
    [Required, MaxLength(8_000)] public string Action { get; init; } = string.Empty;
    [Required, MaxLength(8_000)] public string Result { get; init; } = string.Empty;
}

public sealed record StarStoryListItemResponse(Guid Id, string Title, IReadOnlyCollection<string> Tags, int? LatestScore, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record StarStoryPageResponse(int Total, IReadOnlyCollection<StarStoryListItemResponse> Items);
public sealed record StarStoryDetailResponse(Guid Id, string Title, IReadOnlyCollection<string> Tags, string Situation, string Task, string Action, string Result, int? LatestScore, object? LatestEvaluation, DateTimeOffset? LatestEvaluatedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
