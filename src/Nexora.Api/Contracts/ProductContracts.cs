using System.ComponentModel.DataAnnotations;

namespace Nexora.Api.Contracts;

public sealed record PlanFeatureResponse(string Code, string Name, bool Enabled, int? Limit, bool Unlimited);
public sealed record PlanPriceResponseV2(Guid Id, long AmountMinor, string Currency, int? DurationDays, int? InterviewQuota, IReadOnlyCollection<PlanFeatureResponse> Features);
public sealed record PlanResponseV2(Guid Id, string Code, string Name, string Description, string? Badge, bool IsHighlighted, IReadOnlyCollection<PlanPriceResponseV2> Prices);
public sealed record EntitlementDetailResponse(Guid Id, string PlanCode, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, IReadOnlyCollection<EntitlementFeatureResponse> Features);

public sealed record ScenarioCategoryResponse(Guid Id, string Slug, string Name, string? Description);
public sealed record ScenarioCardResponse(Guid Id, string Slug, string Title, string Summary, string CategorySlug, string CategoryName, string Difficulty, string Competency, int EstimatedMinutes);
public sealed record ScenarioDetailResponse(Guid Id, string Slug, string Title, string Summary, string CategorySlug, string CategoryName, string Difficulty, string Competency, int EstimatedMinutes, string Content);
public sealed record ScenarioPageResponse(int Total, IReadOnlyCollection<ScenarioCardResponse> Items);
public sealed record ScenarioAttemptResponse(Guid Id, Guid ScenarioId, string ScenarioTitle, string Status, string? Answer, object? Evaluation, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);
public sealed record StarAttemptResponse(Guid Id, string Question, string Answer, string Status, object? Evaluation, string? ErrorCode, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record ProgressResponse(
    int CompletedInterviews,
    IReadOnlyCollection<RecentInterviewScoreResponse> RecentInterviewScores,
    double? AverageInterviewScore,
    ProgressStarAveragesResponse? StarAverages,
    int CompletedScenarios,
    double? AverageScenarioScore,
    int CompletedStarAttempts,
    IReadOnlyCollection<RecentActivityResponse> RecentActivity);
public sealed record RecentInterviewScoreResponse(Guid InterviewId, int Score, DateTimeOffset CompletedAt);
public sealed record ProgressStarAveragesResponse(int Situation, int Task, int Action, int Result);
public sealed record RecentActivityResponse(string Kind, Guid ResourceId, DateTimeOffset At);

public sealed record AdminScenarioResponse(Guid Id, string Slug, string Title, string Summary, Guid CategoryId, string Difficulty, string Competency, int EstimatedMinutes, string Content, string Status, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

public sealed record AdminPlanResponse(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string? Badge,
    bool IsHighlighted,
    int SortOrder,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<AdminPlanPriceResponse> Prices);
public sealed record AdminPlanPriceResponse(Guid Id, long AmountMinor, string Currency, int? DurationDays, int? InterviewQuota, bool IsActive, IReadOnlyCollection<AdminPlanFeatureResponse> Features);
public sealed record AdminPlanFeatureResponse(Guid FeatureDefinitionId, string Code, string Name, bool Enabled, int? Limit, bool Unlimited);
public sealed record AdminUserSummaryResponse(Guid Id, string Email, string? DisplayName, IReadOnlyCollection<string> Roles, bool Active, DateTimeOffset CreatedAt, string? CurrentPlanCode, string? EntitlementStatus, DateTimeOffset? EntitlementStartsAt, DateTimeOffset? EntitlementEndsAt);
public sealed record AdminUserPageResponse(Guid? LastId, IReadOnlyCollection<AdminUserSummaryResponse> Users);
public sealed record AdminUserDetailResponse(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyCollection<string> Roles,
    bool Active,
    DateTimeOffset CreatedAt,
    EntitlementDetailResponse? CurrentEntitlement,
    IReadOnlyCollection<OrderResponse> RecentOrders);
public sealed record AdminGrantResponse(Guid EntitlementId, string PlanCode, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record AdminAdjustmentResponse(string FeatureCode, int Quantity, int? Available);
public sealed record AdminFeatureDefinitionResponse(Guid Id, string Code, string Name, string Description, bool IsActive, int SortOrder);

public sealed class AdminPlanCreateRequest
{
    [Required, MaxLength(40)] public string Code { get; init; } = string.Empty;
    [Required, MaxLength(120)] public string Name { get; init; } = string.Empty;
    [MaxLength(500)] public string? Description { get; init; }
    [MaxLength(40)] public string? Badge { get; init; }
    public bool IsHighlighted { get; init; }
}
public sealed class AdminPlanUpdateRequest
{
    [Required, MaxLength(120)] public string Name { get; init; } = string.Empty;
    [MaxLength(500)] public string? Description { get; init; }
    [MaxLength(40)] public string? Badge { get; init; }
    public bool IsHighlighted { get; init; }
    public bool IsActive { get; init; }
}
public sealed class AdminPriceCreateRequest
{
    [Range(0, long.MaxValue)] public long AmountMinor { get; init; }
    [Required, MaxLength(3)] public string Currency { get; init; } = "VND";
    [Range(1, 3650)] public int? DurationDays { get; init; }
    [Range(0, 100_000)] public int? InterviewQuota { get; init; }
}
public sealed class AdminPriceUpdateRequest
{
    [Range(0, long.MaxValue)] public long AmountMinor { get; init; }
    [Required, MaxLength(3)] public string Currency { get; init; } = "VND";
    [Range(1, 3650)] public int? DurationDays { get; init; }
    [Range(0, 100_000)] public int? InterviewQuota { get; init; }
    public bool IsActive { get; init; }
}
public sealed class AdminFeaturesUpdateRequest
{
    [Required, MinLength(1)] public IReadOnlyCollection<AdminFeatureWrite> Features { get; init; } = [];
}
public sealed class AdminFeatureWrite
{
    [Required, MaxLength(40)] public string FeatureCode { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    [Range(0, 100_000)] public int? Limit { get; init; }
}
public sealed class AdminGrantRequest
{
    [Required] public Guid PlanPriceId { get; init; }
    public bool ReplaceCurrent { get; init; }
    [Required, MaxLength(500)] public string Reason { get; init; } = string.Empty;
}
public sealed class AdminAdjustmentRequest
{
    [Required, MaxLength(40)] public string FeatureCode { get; init; } = string.Empty;
    public int Quantity { get; init; }
    [Required, MaxLength(500)] public string Reason { get; init; } = string.Empty;
}
public sealed class ScenarioCategoryCreateRequest
{
    [Required, MaxLength(80)] public string Slug { get; init; } = string.Empty;
    [Required, MaxLength(120)] public string Name { get; init; } = string.Empty;
    [MaxLength(500)] public string? Description { get; init; }
}
public sealed class ScenarioCategoryUpdateRequest
{
    [Required, MaxLength(120)] public string Name { get; init; } = string.Empty;
    [MaxLength(500)] public string? Description { get; init; }
    public bool IsActive { get; init; }
}
public sealed class ScenarioCreateRequest
{
    [Required, MaxLength(120)] public string Slug { get; init; } = string.Empty;
    [Required, MaxLength(200)] public string Title { get; init; } = string.Empty;
    [Required, MaxLength(500)] public string Summary { get; init; } = string.Empty;
    [Required] public Guid CategoryId { get; init; }
    [Required, MaxLength(20)] public string Difficulty { get; init; } = "medium";
    [Required, MaxLength(80)] public string Competency { get; init; } = string.Empty;
    [Range(1, 600)] public int EstimatedMinutes { get; init; } = 15;
    [Required, MaxLength(20_000)] public string Content { get; init; } = string.Empty;
}
public sealed class ScenarioUpdateRequest
{
    [Required, MaxLength(200)] public string Title { get; init; } = string.Empty;
    [Required, MaxLength(500)] public string Summary { get; init; } = string.Empty;
    [Required] public Guid CategoryId { get; init; }
    [Required, MaxLength(20)] public string Difficulty { get; init; } = "medium";
    [Required, MaxLength(80)] public string Competency { get; init; } = string.Empty;
    [Range(1, 600)] public int EstimatedMinutes { get; init; } = 15;
    [Required, MaxLength(20_000)] public string Content { get; init; } = string.Empty;
}
public sealed record ScenarioAttemptCreateRequest(Guid ScenarioId);
public sealed class ScenarioAttemptSubmitRequest
{
    [Required, MaxLength(12_000)] public string Answer { get; init; } = string.Empty;
}
public sealed class StarAttemptCreateRequest
{
    [Required, MaxLength(2_000)] public string Question { get; init; } = string.Empty;
    [Required, MaxLength(12_000)] public string Answer { get; init; } = string.Empty;
}
