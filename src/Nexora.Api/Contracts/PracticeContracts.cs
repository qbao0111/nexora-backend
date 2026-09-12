using System.ComponentModel.DataAnnotations;

namespace Nexora.Api.Contracts;

public sealed record PresignUploadRequest(string FileName, string ContentType, long Size);
public sealed record FinalizeResumeRequest(string UploadToken);
public sealed record CreateJobDescriptionRequest(string Title, string Content);
public sealed record CreateResumeAnalysisRequest(
    Guid ResumeId,
    string Mode,
    Guid? JobDescriptionId,
    string? Industry,
    string? TargetRole,
    string? Seniority);
public sealed class DevelopmentResumeAnalysisRequest
{
    [Required(ErrorMessage = "File là bắt buộc.")]
    public IFormFile? File { get; set; }
    public string JobDescription { get; set; } = string.Empty;
}
public sealed record StartInterviewRequest(
    string Role,
    string Seniority,
    string InterviewType,
    string Difficulty,
    Guid? ResumeId,
    Guid? JobDescriptionId,
    string? Language = null);
public sealed record SubmitAnswerRequest(Guid QuestionId, string Content, int? DurationSeconds);
