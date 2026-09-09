using Nexora.Data.Identity;

namespace Nexora.Data.Practice;

public sealed class ScenarioCategory
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<Scenario> Scenarios { get; } = [];
}

public sealed class Scenario
{
    public Guid Id { get; set; }
    public string Slug { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public Guid CategoryId { get; set; }
    public string Difficulty { get; set; } = string.Empty;
    public string Competency { get; set; } = string.Empty;
    public int EstimatedMinutes { get; set; }
    public string Content { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public ScenarioCategory Category { get; set; } = null!;
    public ICollection<ScenarioAttempt> Attempts { get; } = [];
}

public sealed class ScenarioAttempt
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ScenarioId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? Answer { get; set; }
    public string? EvaluationJson { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public Guid? UsageReservationId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public Scenario Scenario { get; set; } = null!;
}

public sealed class StarAttempt
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? EvaluationJson { get; set; }
    public string ModelVersion { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public Guid? UsageReservationId { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
}

public sealed class StarStory
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string TagsJson { get; set; } = "[]";
    public string Situation { get; set; } = string.Empty;
    public string Task { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Result { get; set; } = string.Empty;
    public int? LatestScore { get; set; }
    public string? LatestEvaluationJson { get; set; }
    public string? LatestModelVersion { get; set; }
    public string? LatestPromptVersion { get; set; }
    public string? LatestSchemaVersion { get; set; }
    public DateTimeOffset? LatestEvaluatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
}
