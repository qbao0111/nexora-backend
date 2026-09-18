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

/// <summary>
/// Server-owned semantics for interview questions. Sequence is ordering only;
/// it never determines whether a question is a follow-up.
/// </summary>
public static class InterviewQuestionValues
{
    public const int FreeQuestionLimit = 3;
    public const string Primary = "primary";
    public const string Followup = "followup";

    public const string SelfIntroduction = "self_introduction";
    public const string BehavioralStar = "behavioral_star";
    public const string MotivationRoleFit = "motivation_role_fit";
    public const string Technical = "technical";
    public const string Behavioral = "behavioral";
    public const string CvTargeted = "cv_targeted";
    public const string JdTargeted = "jd_targeted";
    public const string Scenario = "scenario";

    public static bool IsSupportedKind(string? kind) =>
        string.Equals(kind, Primary, StringComparison.Ordinal) ||
        string.Equals(kind, Followup, StringComparison.Ordinal);

    public static bool IsSupportedInterviewType(string? interviewType) => interviewType?.Trim().ToLowerInvariant() switch
    {
        "technical" or "behavioral" or "scenario" or "cv_targeted" or "jd_targeted" or
        "motivation_role_fit" or "self_introduction" => true,
        _ => false
    };

    /// <summary>
    /// Selects the canonical topic for one of the first three free primary
    /// questions. The server owns this deterministic policy; the AI provider
    /// only generates content for the selected topic.
    /// </summary>
    public static string FreePrimaryTopicForSequence(
        string interviewType,
        int sequence,
        bool hasResume,
        bool hasJobDescription)
    {
        var normalizedType = interviewType.Trim().ToLowerInvariant();
        return sequence switch
        {
            1 => SelfIntroduction,
            2 => normalizedType switch
            {
                "technical" => Technical,
                "behavioral" => BehavioralStar,
                "scenario" => Scenario,
                "cv_targeted" => CvTargeted,
                "jd_targeted" => JdTargeted,
                "motivation_role_fit" => MotivationRoleFit,
                "self_introduction" => MotivationRoleFit,
                _ => SelfIntroduction
            },
            3 => normalizedType switch
            {
                "technical" => hasJobDescription ? JdTargeted : hasResume ? CvTargeted : Technical,
                "behavioral" => MotivationRoleFit,
                "scenario" => Scenario,
                "cv_targeted" => CvTargeted,
                "jd_targeted" => JdTargeted,
                "motivation_role_fit" => MotivationRoleFit,
                "self_introduction" => hasJobDescription ? JdTargeted : hasResume ? CvTargeted : Behavioral,
                _ => SelfIntroduction
            },
            _ => SelfIntroduction
        };
    }

    /// <summary>
    /// Maps the current interview type to a stable topic for the first question
    /// without making the AI provider authoritative for question semantics.
    /// </summary>
    public static string TopicForInterviewType(string interviewType) => interviewType.Trim().ToLowerInvariant() switch
    {
        "behavioral" => BehavioralStar,
        "technical" => Technical,
        "cv_targeted" => CvTargeted,
        "jd_targeted" => JdTargeted,
        "scenario" => Scenario,
        "motivation_role_fit" => MotivationRoleFit,
        "self_introduction" => SelfIntroduction,
        _ => SelfIntroduction
    };

    /// <summary>
    /// Selects the first paid continuation topic from server-owned session context.
    /// The client never supplies this choice and free primary topics remain fixed.
    /// </summary>
    public static string PaidTopicForContext(string interviewType, bool hasResume, bool hasJobDescription) =>
        hasJobDescription
            ? JdTargeted
            : hasResume
                ? CvTargeted
                : interviewType.Trim().ToLowerInvariant() switch
                {
                    "behavioral" => Behavioral,
                    "technical" => Technical,
                    "scenario" => Scenario,
                    _ => Technical
                };
}

public static class InterviewContinuationValues
{
    public const string InProgress = "in_progress";
    public const string UpgradeRequired = "upgrade_required";
    public const string MaxQuestionsReached = "max_questions_reached";
}

public static class InterviewPracticeValues
{
    public const string RepeatQuestion = "repeat_question";
    public const string RubricWeakness = "rubric_weakness";
    public const string Recommendation = "recommendation";
    public const string Manual = "manual";

    public static bool IsSupportedReason(string? reason) => reason switch
    {
        RepeatQuestion or RubricWeakness or Recommendation or Manual => true,
        _ => false
    };

    public static bool IsSupportedRubricFocus(string? focus) => focus?.Trim().ToLowerInvariant() switch
    {
        "correctness" or "structure" or "completeness" or "clarity" => true,
        _ => false
    };
}

public enum ResumeAnalysisMode
{
    JobTargeted,
    FieldBenchmark
}

public static class ResumeAnalysisModes
{
    public const string JobTargeted = "job_targeted";
    public const string FieldBenchmark = "field_benchmark";

    public static bool TryParse(string? value, out ResumeAnalysisMode mode)
    {
        if (string.Equals(value?.Trim(), JobTargeted, StringComparison.OrdinalIgnoreCase))
        {
            mode = ResumeAnalysisMode.JobTargeted;
            return true;
        }

        if (string.Equals(value?.Trim(), FieldBenchmark, StringComparison.OrdinalIgnoreCase))
        {
            mode = ResumeAnalysisMode.FieldBenchmark;
            return true;
        }

        mode = default;
        return false;
    }

    public static string ToWireValue(this ResumeAnalysisMode mode) => mode switch
    {
        ResumeAnalysisMode.JobTargeted => JobTargeted,
        ResumeAnalysisMode.FieldBenchmark => FieldBenchmark,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}

public sealed record StartResumeAnalysisCommand(
    Guid? ResumeId,
    string Mode,
    Guid? JobDescriptionId,
    string? Industry,
    string? TargetRole,
    string? Seniority,
    Guid? CareerGoalId = null);

public sealed record ResumeAnalysisContext(
    ResumeAnalysisMode Mode,
    string? JobDescription,
    string? Industry,
    string? TargetRole,
    string? Seniority);

public sealed record UploadIntent(string Token, string UploadUrl, DateTimeOffset ExpiresAt);
public sealed record PendingUpload(string Token, Guid UserId, string StorageKey, string FileName, string ContentType, long Size, string Checksum);

/// <summary>
/// Provider-neutral durable state for a browser upload capability. The raw token and
/// any signed URL are deliberately not part of this record and must never be persisted.
/// </summary>
public sealed record UploadIntentState(
    Guid Id,
    Guid UserId,
    string StorageKey,
    string FileName,
    string ContentType,
    long ExpectedSize,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int Version,
    long? ActualSize,
    string? Checksum,
    DateTimeOffset? CompletedAt)
{
    public bool IsCompleted => CompletedAt is not null;
}

public interface IUploadIntentStore
{
    Task<UploadIntentState?> FindByTokenHashAsync(
        Guid userId,
        string tokenHash,
        CancellationToken cancellationToken);

    Task<UploadIntentState> CreateAsync(
        UploadIntentState state,
        string tokenHash,
        CancellationToken cancellationToken);

    Task<UploadIntentState?> CompleteAsync(
        Guid userId,
        string tokenHash,
        long actualSize,
        string checksum,
        CancellationToken cancellationToken);
}

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
    IReadOnlyCollection<string> Warnings,
    string? SchemaVersion = null);

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
    string BuildResumeAnalysisContext(ResumeProfile profile, ResumeAnalysisContext context);
    string BuildInterviewQuestionContext(
        string role,
        string seniority,
        string interviewType,
        string difficulty,
        string? jobDescription,
        ResumeProfile? profile,
        int questionSequence = 1,
        string? questionTopic = null);
    string BuildAnswerEvaluationContext(
        string role,
        string seniority,
        string interviewType,
        string? jobDescription,
        string question,
        string answer,
        ResumeProfile? profile,
        int questionSequence = 1,
        bool isFollowUp = false,
        IReadOnlyCollection<string>? followupTargetElements = null,
        string? questionTopic = null);
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
public sealed record ResumeAnalysisContextView(
    string Mode,
    string? Industry,
    string? TargetRole,
    string? Seniority);

public sealed record ResumeAnalysisView(
    Guid Id,
    string Status,
    object? Result,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode = null,
    string? Mode = null,
    ResumeAnalysisContextView? Context = null,
    int ResumeVersion = 0,
    int? JobDescriptionVersion = null,
    string? ModelVersion = null,
    string? PromptVersion = null,
    string? SchemaVersion = null,
    string? RubricVersion = null,
    string? ProfileModelVersion = null,
    string? ProfilePromptVersion = null,
    string? ProfileSchemaVersion = null);
public sealed record DevelopmentResumeAnalysisView(ResumeView Resume, JobDescriptionView JobDescription, ResumeAnalysisView Analysis);

public sealed record StartInterviewCommand(
    string? Role,
    string? Seniority,
    string InterviewType,
    string Difficulty,
    Guid? ResumeId,
    Guid? JobDescriptionId,
    Guid? CareerGoalId = null);

public sealed record InterviewHistoryPage(
    IReadOnlyCollection<InterviewHistoryItem> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasNextPage);

public sealed record InterviewHistoryItem(
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

public sealed record ResumeAnalysisHistoryPage(
    IReadOnlyCollection<ResumeAnalysisHistoryItem> Items,
    int Page,
    int PageSize,
    int TotalCount,
    bool HasNextPage);

public sealed record ResumeAnalysisHistoryItem(
    Guid Id,
    Guid ResumeId,
    string Mode,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    ResumeAnalysisContextView? Context,
    string? ErrorCode);

public sealed record PracticeAgainCommand(Guid? QuestionId, string? Focus, string? Reason = null);

public sealed record QuestionView(
    Guid Id,
    int Sequence,
    string Kind,
    string Topic,
    Guid? ParentQuestionId,
    string Content,
    DateTimeOffset CreatedAt);
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
    DateTimeOffset UpdatedAt,
    InterviewContinuationView? Continuation = null);

public sealed record AnswerResult(
    AnswerView Answer,
    QuestionView? NextQuestion,
    bool IsComplete,
    InterviewContinuationView? Continuation = null);
public sealed record InterviewContinuationView(
    string State,
    bool CanFinishNow,
    bool CanUpgradeAndContinue);
public sealed record InterviewQuestionReviewView(
    Guid QuestionId,
    int Sequence,
    string Kind,
    string Topic,
    Guid? ParentQuestionId,
    string Question,
    string Answer,
    IReadOnlyCollection<RubricScore> Rubric,
    string Feedback,
    StarEvaluation? Star,
    IReadOnlyCollection<string> Strengths,
    IReadOnlyCollection<string> Improvements,
    string? SuggestedImprovedAnswer,
    SampleInterviewAnswer? SampleAnswer = null);
public sealed record SuggestedImprovedAnswerView(
    Guid QuestionId,
    int Sequence,
    string Answer);
public sealed record ReportSampleView(
    int AnsweredQuestions,
    int IssuedQuestions,
    bool IsPartial);
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
    object? StarSummary = null,
    IReadOnlyCollection<InterviewQuestionReviewView>? QuestionReviews = null,
    IReadOnlyCollection<SuggestedImprovedAnswerView>? SuggestedImprovedAnswers = null,
    ReportSampleView? Sample = null);
public sealed record DashboardView(object? Billing, IReadOnlyCollection<InterviewSummary> Interviews, IReadOnlyCollection<ReportSummary> Reports);
public sealed record InterviewSummary(Guid Id, string Role, string Status, DateTimeOffset UpdatedAt);
public sealed record ReportSummary(Guid Id, Guid InterviewId, int OverallScore, DateTimeOffset CreatedAt);

public interface IPracticeService
{
    Task<DevelopmentResumeAnalysisView> CreateDevelopmentResumeAnalysisAsync(
        Guid userId, Stream content, string fileName, string contentType, long size, string jobDescription, string idempotencyKey,
        CancellationToken cancellationToken);
    Task<ResumeView> CreateResumeAsync(Guid userId, string uploadToken, CancellationToken cancellationToken);
    Task DeleteResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ResumeView>> GetResumesAsync(Guid userId, CancellationToken cancellationToken);
    Task<ResumeView> GetResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken);
    Task<JobDescriptionView> CreateJobDescriptionAsync(Guid userId, string title, string content, CancellationToken cancellationToken, string? idempotencyKey = null);
    Task<IReadOnlyList<JobDescriptionView>> GetJobDescriptionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<JobDescriptionView> GetJobDescriptionAsync(Guid userId, Guid jobDescriptionId, CancellationToken cancellationToken);
    Task<ResumeAnalysisView> StartResumeAnalysisAsync(Guid userId, StartResumeAnalysisCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<ResumeAnalysisView> GetResumeAnalysisAsync(Guid userId, Guid analysisId, CancellationToken cancellationToken);
    Task<ResumeAnalysisHistoryPage> GetResumeAnalysisHistoryAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);
    Task<InterviewView> StartInterviewAsync(Guid userId, StartInterviewCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewHistoryPage> GetInterviewHistoryAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken);
    Task<InterviewView> GetInterviewAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken);
    Task<InterviewView> PracticeAgainAsync(Guid userId, Guid interviewId, PracticeAgainCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<AnswerResult> SubmitAnswerAsync(Guid userId, Guid interviewId, Guid questionId, string content, int? durationSeconds, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> ContinueInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> CompleteInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
    Task<InterviewView> RetryReportAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken);
    Task<ReportView> GetReportAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken);
    Task<DashboardView> GetDashboardAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IPracticeJobProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}
