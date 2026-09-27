namespace Nexora.Business.ContentReports;

public static class ContentReportValues
{
    public const string Pending = "pending";
    public const string Reviewing = "reviewing";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";

    public const string Offensive = "offensive";
    public const string Inaccurate = "inaccurate";
    public const string Irrelevant = "irrelevant";
    public const string PrivacyViolation = "privacy_violation";
    public const string Discriminatory = "discriminatory";
    public const string Other = "other";

    public const string InterviewQuestion = "interview_question";
    public const string InterviewAnswerEvaluation = "interview_answer_evaluation";
    public const string InterviewReport = "interview_report";
    public const string ResumeAnalysis = "resume_analysis";
    public const string ScenarioEvaluation = "scenario_evaluation";
    public const string StarEvaluation = "star_evaluation";
}

public static class ContentReportRules
{
    public const int MaximumDescriptionLength = 1_000;
    public const int MaximumSnapshotLength = 40_000;
    public const int MaximumResolutionNoteLength = 1_000;
    public const int MaximumPageSize = 100;
}

public sealed record SubmitContentReportCommand(string ContentType, Guid ContentId, string ReasonCode, string? Description);
public sealed record ContentReportReceipt(Guid ReportId, DateTimeOffset ReceivedAt);
public sealed record ContentReportQuery(
    string? Status,
    string? ContentType,
    string? ReasonCode,
    DateTimeOffset? From,
    DateTimeOffset? To,
    int Page,
    int PageSize);

public sealed record ContentReportAdminItem(
    Guid Id,
    Guid ReporterUserId,
    string ContentType,
    Guid ContentId,
    string ReasonCode,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReviewedAt,
    Guid? ModeratorUserId);

public sealed record ContentReportAdminDetail(
    ContentReportAdminItem Report,
    string? Description,
    string? ContentSnapshot,
    string? ResolutionCode,
    string? ResolutionNote,
    DateTimeOffset? ResolvedAt);

public sealed record ContentReportAdminPage(
    IReadOnlyCollection<ContentReportAdminItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public interface IContentReportService
{
    Task<ContentReportReceipt> SubmitAsync(Guid reporterUserId, SubmitContentReportCommand command, CancellationToken cancellationToken);
    Task<ContentReportAdminPage> GetPageAsync(ContentReportQuery query, CancellationToken cancellationToken);
    Task<ContentReportAdminDetail> GetAsync(Guid reportId, CancellationToken cancellationToken);
    Task<ContentReportAdminDetail> ReviewAsync(Guid moderatorUserId, Guid reportId, CancellationToken cancellationToken);
    Task<ContentReportAdminDetail> ResolveAsync(
        Guid moderatorUserId,
        Guid reportId,
        string outcome,
        string resolutionCode,
        string? resolutionNote,
        CancellationToken cancellationToken);
}
