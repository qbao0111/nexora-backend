using Nexora.Business.Ai;

namespace Nexora.Business.Practice;

public static class PracticeValues
{
    public const string Uploaded = "uploaded";
    public const string Extracting = "extracting";
    public const string OcrFallback = "ocr_fallback";
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

public enum DocumentExtractionMethod
{
    PdfText,
    PdfLayoutReconstructed,
    DocxOpenXml,
    GeminiOcr
}

public enum DocumentExtractionQuality
{
    Good,
    Suspicious,
    Failed
}

public sealed class DocumentExtractionQualityOptions
{
    public int MinimumWords { get; set; } = 5;
    public int MinimumCharactersPerPage { get; set; } = 20;
    public double GoodScore { get; set; } = 0.75;
    public double SuspiciousScore { get; set; } = 0.40;
    public double MinimumPrintableRatio { get; set; } = 0.95;
    public double MaximumReplacementRatio { get; set; } = 0.01;
    public double MaximumControlRatio { get; set; } = 0.01;
    public double MaximumRepeatedLineRatio { get; set; } = 0.40;
}

public sealed record DocumentExtractionResult(
    string Text,
    int PageCount,
    int CharacterCount,
    int WordCount,
    DocumentExtractionMethod ExtractionMethod,
    double QualityScore,
    DocumentExtractionQuality Quality,
    double PrintableCharacterRatio,
    double ReplacementCharacterRatio,
    double ControlCharacterRatio,
    double AverageUsableCharactersPerPage,
    double RepeatedLineRatio,
    IReadOnlyCollection<string> Warnings);

public interface IDetailedDocumentExtractor
{
    Task<DocumentExtractionResult> ExtractDetailedAsync(Stream content, string contentType, CancellationToken cancellationToken);

    /// <summary>
    /// Applies the same normalization and deterministic quality gate to text returned by a fallback provider.
    /// </summary>
    DocumentExtractionResult EvaluateExtractedText(
        string text,
        int pageCount,
        DocumentExtractionMethod method,
        IEnumerable<string>? warnings = null);
}

public sealed record DocumentOcrResult(
    string ExtractedText,
    ResumeProfile Profile,
    int PageCount,
    IReadOnlyCollection<string> Warnings);

public interface IDocumentOcrProvider
{
    Task<DocumentOcrResult> ExtractAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken);
}

public sealed record ResumeExperience(
    string? Company,
    string? Role,
    string? Start,
    string? End,
    IReadOnlyCollection<string> Highlights);

public sealed record ResumeEducation(
    string? Institution,
    string? Degree,
    string? Start,
    string? End,
    IReadOnlyCollection<string> Details);

public sealed record ResumeProject(
    string? Name,
    string? Role,
    IReadOnlyCollection<string> Technologies,
    IReadOnlyCollection<string> Highlights);

public sealed record ResumeProfile(
    string? Summary,
    IReadOnlyCollection<string> Skills,
    IReadOnlyCollection<ResumeExperience> Experiences,
    IReadOnlyCollection<ResumeEducation> Education,
    IReadOnlyCollection<ResumeProject> Projects,
    IReadOnlyCollection<string> Certifications,
    IReadOnlyCollection<string> Languages);

public interface IResumeContextBuilder
{
    string BuildProfileExtractionContext(string rawExtractedText);
    string BuildResumeAnalysisContext(ResumeProfile profile, string jobDescription);
    string BuildInterviewQuestionContext(string role, string seniority, string interviewType, string difficulty, string? jobDescription, ResumeProfile? profile);
    string BuildAnswerEvaluationContext(string role, string seniority, string interviewType, string? jobDescription, string question, string answer, ResumeProfile? profile);
    string BuildFollowupQuestionContext(string role, string seniority, string interviewType, string? jobDescription, string question, string answer, StarEvaluation? star, ResumeProfile? profile);
    string BuildReportContext(string transcript, ResumeProfile? profile);
}

public sealed record ResumeView(
    Guid Id,
    string FileName,
    string ContentType,
    long Size,
    string Status,
    DateTimeOffset CreatedAt,
    string? ErrorCode = null,
    string? ErrorMessage = null);
public sealed record JobDescriptionView(Guid Id, string Title, string Content, DateTimeOffset CreatedAt);
public sealed record ResumeAnalysisView(
    Guid Id,
    string Status,
    object? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode = null);
public sealed record DevelopmentResumeAnalysisView(ResumeView Resume, JobDescriptionView JobDescription, ResumeAnalysisView Analysis);

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
public sealed record ReportView(
    Guid Id,
    Guid InterviewId,
    int OverallScore,
    object Rubric,
    object Strengths,
    object Gaps,
    object ActionPlan,
    string Disclaimer,
    DateTimeOffset CreatedAt,
    object? StarSummary = null);
public sealed record DashboardView(object? Billing, IReadOnlyCollection<InterviewSummary> Interviews, IReadOnlyCollection<ReportSummary> Reports);
public sealed record InterviewSummary(Guid Id, string Role, string Status, DateTimeOffset UpdatedAt);
public sealed record ReportSummary(Guid Id, Guid InterviewId, int OverallScore, DateTimeOffset CreatedAt);

public interface IPracticeService
{
    Task<DevelopmentResumeAnalysisView> CreateDevelopmentResumeAnalysisAsync(
        Guid userId, Stream content, string fileName, string contentType, long size, string jobDescription, string idempotencyKey,
        CancellationToken cancellationToken);
    Task<ResumeView> CreateResumeAsync(Guid userId, string uploadToken, CancellationToken cancellationToken);
    Task<ResumeView> GetResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken);
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
