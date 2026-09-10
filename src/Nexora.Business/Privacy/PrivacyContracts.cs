using System.Text.Json;
using Nexora.Business.Billing;
using Nexora.Business.Practice;

namespace Nexora.Business.Privacy;

public static class PrivacyValues
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public sealed record ExportProfile(Guid Id, string Email, string? DisplayName, DateTimeOffset CreatedAt);
public sealed record ExportResume(Guid Id, string FileName, string ContentType, long Size, string Status, DateTimeOffset CreatedAt);
public sealed record ExportJobDescription(Guid Id, string Title, string Content, DateTimeOffset CreatedAt);
public sealed record ExportAnalysis(
    Guid Id,
    Guid ResumeId,
    Guid? JobDescriptionId,
    string Status,
    JsonElement? Result,
    DateTimeOffset CreatedAt,
    string Mode,
    ResumeAnalysisContextView? Context,
    int ResumeVersion,
    int? JobDescriptionVersion,
    string ModelVersion,
    string PromptVersion,
    string SchemaVersion,
    string? RubricVersion,
    string? ProfileModelVersion,
    string? ProfilePromptVersion,
    string? ProfileSchemaVersion);
public sealed record ExportInterview(InterviewView Interview, ReportView? Report);
public sealed record CoreDataExport(
    DateTimeOffset GeneratedAt,
    ExportProfile Profile,
    BillingSummary Billing,
    IReadOnlyCollection<ExportResume> Resumes,
    IReadOnlyCollection<ExportJobDescription> JobDescriptions,
    IReadOnlyCollection<ExportAnalysis> Analyses,
    IReadOnlyCollection<ExportInterview> Interviews);
public sealed record DeletionRequestView(Guid Id, string Status, int Attempts, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

public interface IPrivacyService
{
    Task<CoreDataExport> ExportAsync(Guid userId, CancellationToken cancellationToken);
    Task<DeletionRequestView> RequestDeletionAsync(Guid userId, string idempotencyKey, CancellationToken cancellationToken);
}

public interface IPrivacyJobProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}
