using System.ComponentModel.DataAnnotations;

namespace Nexora.Api.Contracts;

public sealed record PresignUploadRequest(string FileName, string ContentType, long Size);
public sealed record FinalizeResumeRequest(string UploadToken);
public sealed record CreateJobDescriptionRequest(string Title, string Content);
public sealed record CreateResumeAnalysisRequest(
    Guid? ResumeId,
    string Mode,
    Guid? JobDescriptionId,
    string? Industry,
    string? TargetRole,
    string? Seniority,
    Guid? CareerGoalId = null);
public sealed class DevelopmentResumeAnalysisRequest
{
    [Required(ErrorMessage = "File là bắt buộc.")]
    public IFormFile? File { get; set; }
    public string JobDescription { get; set; } = string.Empty;
}
public sealed record StartInterviewRequest(
    string? Role,
    string? Seniority,
    string InterviewType,
    string Difficulty,
    Guid? ResumeId,
    Guid? JobDescriptionId,
    Guid? CareerGoalId = null);
public sealed record SubmitAnswerRequest(Guid QuestionId, string Content, int? DurationSeconds);
public sealed record PracticeAgainRequest(Guid? QuestionId, string? Focus, string? Reason = null);

public sealed record InterviewHistoryItemResponse(
    Guid Id,
    string Status,
    string Role,
    string Seniority,
    string InterviewType,
    string Difficulty,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    int AnsweredQuestionCount,
    int IssuedQuestionCount,
    bool ReportAvailable,
    Guid? CareerGoalId,
    Guid? SourceInterviewId,
    Guid? SourceQuestionId,
    string? PracticeReason,
    string? FocusTopic);

public sealed record InterviewHistoryResponse(
    IReadOnlyCollection<InterviewHistoryItemResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasNextPage);

public sealed record ResumeAnalysisHistoryItemResponse(
    Guid Id,
    Guid ResumeId,
    string Mode,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    ResumeAnalysisContextResponse? Context,
    string? ErrorCode);

public sealed record ResumeAnalysisContextResponse(
    string Mode,
    string? Industry,
    string? TargetRole,
    string? Seniority);

public sealed record ResumeAnalysisHistoryResponse(
    IReadOnlyCollection<ResumeAnalysisHistoryItemResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasNextPage);
