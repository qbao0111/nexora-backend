namespace Nexora.Business.Practice;

public static class PracticeValues
{
    public const string Uploaded = "uploaded";
    public const string Ready = "ready";
    public const string Failed = "failed";
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Starting = "starting";
    public const string Active = "active";
    public const string Completing = "completing";
}

public sealed record UploadIntent(string Token, string UploadUrl, DateTimeOffset ExpiresAt);
public sealed record PendingUpload(string Token, Guid UserId, string StorageKey, string FileName, string ContentType, long Size, string Checksum);

public interface IUploadProvider
{
    Task<UploadIntent> CreateIntentAsync(Guid userId, string fileName, string contentType, long size, CancellationToken cancellationToken);
    Task UploadAsync(string token, Stream content, CancellationToken cancellationToken);
    Task<PendingUpload> GetCompletedAsync(Guid userId, string token, CancellationToken cancellationToken);
}

public interface IDocumentExtractor
{
    Task<string> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken);
}

public sealed record ResumeView(Guid Id, string FileName, string ContentType, long Size, string Status, DateTimeOffset CreatedAt);
public sealed record JobDescriptionView(Guid Id, string Title, string Content, DateTimeOffset CreatedAt);
public sealed record ResumeAnalysisView(Guid Id, string Status, object? Result, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record StartInterviewCommand(
    string Role,
    string Seniority,
    string InterviewType,
    string Difficulty,
    Guid? ResumeId,
    Guid? JobDescriptionId);

public sealed record QuestionView(Guid Id, int Sequence, string Content, DateTimeOffset CreatedAt);
public sealed record AnswerView(Guid Id, Guid QuestionId, string Content, int? DurationSeconds, object? Evaluation, DateTimeOffset CreatedAt);
public sealed record InterviewView(
    Guid Id,
    string Status,
    string Role,
    string Seniority,
    string InterviewType,
    string Difficulty,
    int Version,
    IReadOnlyCollection<QuestionView> Questions,
    IReadOnlyCollection<AnswerView> Answers,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AnswerResult(AnswerView Answer, QuestionView? NextQuestion, bool IsComplete);
public sealed record ReportView(Guid Id, Guid InterviewId, int OverallScore, object Rubric, object Strengths, object Gaps, object ActionPlan, string Disclaimer, DateTimeOffset CreatedAt);
public sealed record DashboardView(object? Billing, IReadOnlyCollection<InterviewSummary> Interviews, IReadOnlyCollection<ReportSummary> Reports);
public sealed record InterviewSummary(Guid Id, string Role, string Status, DateTimeOffset UpdatedAt);
public sealed record ReportSummary(Guid Id, Guid InterviewId, int OverallScore, DateTimeOffset CreatedAt);

public interface IPracticeService
{
    Task<ResumeView> CreateResumeAsync(Guid userId, string uploadToken, CancellationToken cancellationToken);
    Task<JobDescriptionView> CreateJobDescriptionAsync(Guid userId, string title, string content, CancellationToken cancellationToken);
    Task<ResumeAnalysisView> StartResumeAnalysisAsync(Guid userId, Guid resumeId, Guid jobDescriptionId, string idempotencyKey, CancellationToken cancellationToken);
    Task<ResumeAnalysisView> GetResumeAnalysisAsync(Guid userId, Guid analysisId, CancellationToken cancellationToken);
    Task<InterviewView> StartInterviewAsync(Guid userId, StartInterviewCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> GetInterviewAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken);
    Task<AnswerResult> SubmitAnswerAsync(Guid userId, Guid interviewId, Guid questionId, string content, int? durationSeconds, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> CompleteInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
    Task<ReportView> GetReportAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken);
    Task<DashboardView> GetDashboardAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IPracticeJobProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}
