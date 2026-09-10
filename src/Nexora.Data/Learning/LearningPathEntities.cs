using Nexora.Data.Career;
using Nexora.Data.Identity;

namespace Nexora.Data.Learning;

public sealed class LearningPath
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CareerGoalId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public CareerGoal CareerGoal { get; set; } = null!;
    public ICollection<LearningPathMilestone> Milestones { get; } = [];
}

public sealed class LearningPathMilestone
{
    public Guid Id { get; set; }
    public Guid LearningPathId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public LearningPath LearningPath { get; set; } = null!;
    public ICollection<LearningPathActivity> Activities { get; } = [];
}

public sealed class LearningPathActivity
{
    public Guid Id { get; set; }
    public Guid LearningPathId { get; set; }
    public Guid LearningPathMilestoneId { get; set; }
    public string ActivityKey { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? CompetencyCode { get; set; }
    public Guid? ResourceId { get; set; }
    public string? ExternalUrl { get; set; }
    public int Priority { get; set; }
    public int SortOrder { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public LearningPath LearningPath { get; set; } = null!;
    public LearningPathMilestone Milestone { get; set; } = null!;
}
