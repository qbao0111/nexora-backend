using Nexora.Data.Billing;
using Nexora.Data.Identity;

namespace Nexora.Data.Practice;

public sealed class StoredFile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string StorageKey { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Checksum { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
}

public sealed class ResumeRecord
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid StoredFileId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ExtractedText { get; set; }
    public string? StructuredProfile { get; set; }
    public string? ProfileModelVersion { get; set; }
    public string? ProfilePromptVersion { get; set; }
    public string? ProfileSchemaVersion { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public StoredFile StoredFile { get; set; } = null!;
}

public sealed class JobDescription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
}

public sealed class ResumeAnalysis
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ResumeId { get; set; }
    public Guid JobDescriptionId { get; set; }
    public int ResumeVersion { get; set; }
    public int JobDescriptionVersion { get; set; }
    public string Status { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public string? Result { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public ResumeRecord Resume { get; set; } = null!;
    public JobDescription JobDescription { get; set; } = null!;
}

public sealed class InterviewSession
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? ResumeId { get; set; }
    public Guid? JobDescriptionId { get; set; }
    public Guid ReservationEventId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Seniority { get; set; } = string.Empty;
    public string InterviewType { get; set; } = string.Empty;
    public string Difficulty { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public ResumeRecord? Resume { get; set; }
    public JobDescription? JobDescription { get; set; }
    public UsageEvent ReservationEvent { get; set; } = null!;
    public ICollection<InterviewQuestion> Questions { get; } = [];
    public ICollection<InterviewAnswer> Answers { get; } = [];
    public InterviewReport? Report { get; set; }
}

public sealed class InterviewQuestion
{
    public Guid Id { get; set; }
    public Guid InterviewSessionId { get; set; }
    public int Sequence { get; set; }
    public string Content { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public InterviewSession InterviewSession { get; set; } = null!;
    public InterviewAnswer? Answer { get; set; }
}

public sealed class InterviewAnswer
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid InterviewSessionId { get; set; }
    public Guid QuestionId { get; set; }
    public string Content { get; set; } = string.Empty;
    public int? DurationSeconds { get; set; }
    public string Evaluation { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public InterviewSession InterviewSession { get; set; } = null!;
    public InterviewQuestion Question { get; set; } = null!;
}

public sealed class InterviewReport
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid InterviewSessionId { get; set; }
    public int OverallScore { get; set; }
    public string Rubric { get; set; } = string.Empty;
    public string Strengths { get; set; } = string.Empty;
    public string Gaps { get; set; } = string.Empty;
    public string ActionPlan { get; set; } = string.Empty;
    public string Disclaimer { get; set; } = string.Empty;
    public string ModelVersion { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string RubricVersion { get; set; } = string.Empty;
    public string SchemaVersion { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public InterviewSession InterviewSession { get; set; } = null!;
}
