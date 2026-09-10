using System.ComponentModel.DataAnnotations;

namespace Nexora.Api.Contracts;

public sealed class UpdateLearningPathActivityRequest
{
    [Required]
    public string Status { get; init; } = string.Empty;
}

public sealed record LearningPathResponse(
    Guid Id,
    Guid CareerGoalId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    LearningPathProgressResponse Progress,
    IReadOnlyCollection<LearningPathMilestoneResponse> Milestones);

public sealed record LearningPathProgressResponse(
    int CompletedActivityCount,
    int TotalActivityCount,
    int Percentage);

public sealed record LearningPathMilestoneResponse(
    Guid Id,
    string Code,
    string Title,
    int Order,
    string Status,
    IReadOnlyCollection<LearningPathActivityResponse> Activities);

public sealed record LearningPathActivityResponse(
    Guid Id,
    string Type,
    string Title,
    string Description,
    string? CompetencyCode,
    Guid? ResourceId,
    string? ExternalUrl,
    int Priority,
    string Status,
    int Order,
    DateTimeOffset? CompletedAt);
