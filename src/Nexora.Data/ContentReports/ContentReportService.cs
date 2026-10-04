using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Common;
using Nexora.Business.ContentReports;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Skills;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.ContentReports;

public sealed class ContentReportService(
    NexoraDbContext dbContext, TimeProvider timeProvider, ISkillProfileService skillProfiles) : IContentReportService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> Reasons =
    [
        ContentReportValues.Offensive,
        ContentReportValues.Inaccurate,
        ContentReportValues.Irrelevant,
        ContentReportValues.PrivacyViolation,
        ContentReportValues.Discriminatory,
        ContentReportValues.Other
    ];
    private static readonly HashSet<string> Outcomes = [ContentReportValues.Resolved, ContentReportValues.Dismissed];
    private static readonly HashSet<string> ResolutionCodes = ["content_corrected", "content_removed", "no_action", "other"];

    public async Task<ContentReportReceipt> SubmitAsync(
        Guid reporterUserId,
        SubmitContentReportCommand command,
        CancellationToken cancellationToken)
    {
        var contentType = Normalize(command.ContentType);
        var reasonCode = Normalize(command.ReasonCode);
        var description = string.IsNullOrWhiteSpace(command.Description) ? null : command.Description.Trim();
        if (!IsSupportedType(contentType))
            throw Validation("Loại nội dung được báo cáo không hợp lệ.");
        if (command.ContentId == Guid.Empty)
            throw Validation("ID nội dung không hợp lệ.");
        if (!Reasons.Contains(reasonCode))
            throw Validation("Lý do báo cáo không hợp lệ.");
        if (description?.Length > ContentReportRules.MaximumDescriptionLength)
            throw Validation("Mô tả báo cáo tối đa 1000 ký tự.");

        var snapshot = await ResolveSnapshotAsync(reporterUserId, contentType, command.ContentId, cancellationToken);
        if (snapshot is null)
            throw NotFound();
        if (snapshot.Length > ContentReportRules.MaximumSnapshotLength)
            throw new BusinessException("CONTENT_NOT_REPORTABLE", "Không thể tạo báo cáo cho nội dung này.", BusinessErrorKind.Conflict);

        var report = new ContentReport
        {
            Id = Guid.NewGuid(),
            ReporterUserId = reporterUserId,
            ContentType = contentType,
            ContentId = command.ContentId,
            ReasonCode = reasonCode,
            Description = description,
            ContentSnapshot = snapshot,
            Status = ContentReportValues.Pending,
            CreatedAt = timeProvider.GetUtcNow()
        };
        dbContext.ContentReports.Add(report);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ContentReportReceipt(report.Id, report.CreatedAt);
    }

    public async Task<ContentReportAdminPage> GetPageAsync(ContentReportQuery query, CancellationToken cancellationToken)
    {
        var status = OptionalFilter(query.Status, [ContentReportValues.Pending, ContentReportValues.Reviewing,
            ContentReportValues.Resolved, ContentReportValues.Dismissed], "Trạng thái báo cáo không hợp lệ.");
        var contentType = OptionalFilter(query.ContentType, ContentTypes, "Loại nội dung không hợp lệ.");
        var reasonCode = OptionalFilter(query.ReasonCode, Reasons, "Lý do báo cáo không hợp lệ.");
        if (query.From.HasValue && query.To.HasValue && query.From > query.To)
            throw Validation("Khoảng thời gian báo cáo không hợp lệ.");
        if (query.Page is < 1 or > 100_000)
            throw Validation("Số trang không hợp lệ.");

        var pageSize = query.PageSize < 1 ? 20 : Math.Min(query.PageSize, ContentReportRules.MaximumPageSize);
        var reports = dbContext.ContentReports.AsNoTracking().AsQueryable();
        if (status is not null) reports = reports.Where(item => item.Status == status);
        if (contentType is not null) reports = reports.Where(item => item.ContentType == contentType);
        if (reasonCode is not null) reports = reports.Where(item => item.ReasonCode == reasonCode);
        int total;
        ContentReportAdminItem[] items;
        if (dbContext.Database.IsNpgsql())
        {
            if (query.From.HasValue) reports = reports.Where(item => item.CreatedAt >= query.From.Value);
            if (query.To.HasValue) reports = reports.Where(item => item.CreatedAt <= query.To.Value);
            total = await reports.CountAsync(cancellationToken);
            items = await ProjectAdmin(reports.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id))
                .Skip((query.Page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        }
        else
        {
            // SQLite cannot order DateTimeOffset; it is only used for local tests.
            var materialized = await ProjectAdmin(reports).ToArrayAsync(cancellationToken);
            var filtered = materialized.Where(item => !query.From.HasValue || item.CreatedAt >= query.From.Value)
                .Where(item => !query.To.HasValue || item.CreatedAt <= query.To.Value)
                .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).ToArray();
            total = filtered.Length;
            items = filtered.Skip((query.Page - 1) * pageSize).Take(pageSize).ToArray();
        }
        return new ContentReportAdminPage(items, query.Page, pageSize, total);
    }

    public async Task<ContentReportAdminDetail> GetAsync(Guid reportId, CancellationToken cancellationToken)
    {
        var report = await dbContext.ContentReports.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == reportId, cancellationToken) ?? throw NotFound();
        return ToAdminDetail(report);
    }

    public async Task<ContentReportAdminDetail> ReviewAsync(
        Guid moderatorUserId,
        Guid reportId,
        CancellationToken cancellationToken)
    {
        var report = await dbContext.ContentReports.SingleOrDefaultAsync(item => item.Id == reportId, cancellationToken)
            ?? throw NotFound();
        if (report.Status == ContentReportValues.Reviewing && report.ModeratorUserId == moderatorUserId)
            return ToAdminDetail(report);
        if (report.Status != ContentReportValues.Pending)
            throw Conflict("Báo cáo không còn ở trạng thái chờ xử lý.");

        report.Status = ContentReportValues.Reviewing;
        report.ModeratorUserId = moderatorUserId;
        report.ReviewedAt = timeProvider.GetUtcNow();
        report.Version++;
        await SaveModerationAsync(report, moderatorUserId, "content_report.review", cancellationToken);
        return ToAdminDetail(report);
    }

    public async Task<ContentReportAdminDetail> ResolveAsync(
        Guid moderatorUserId,
        Guid reportId,
        string outcome,
        string resolutionCode,
        string? resolutionNote,
        CancellationToken cancellationToken)
    {
        outcome = Normalize(outcome);
        resolutionCode = Normalize(resolutionCode);
        resolutionNote = string.IsNullOrWhiteSpace(resolutionNote) ? null : resolutionNote.Trim();
        if (!Outcomes.Contains(outcome) || !ResolutionCodes.Contains(resolutionCode))
            throw Validation("Kết quả xử lý báo cáo không hợp lệ.");
        if (resolutionNote?.Length > ContentReportRules.MaximumResolutionNoteLength)
            throw Validation("Ghi chú xử lý tối đa 1000 ký tự.");

        var report = await dbContext.ContentReports.SingleOrDefaultAsync(item => item.Id == reportId, cancellationToken)
            ?? throw NotFound();
        if (report.Status == outcome && report.ModeratorUserId == moderatorUserId &&
            report.ResolutionCode == resolutionCode && report.ResolutionNote == resolutionNote)
            return ToAdminDetail(report);
        if (report.Status != ContentReportValues.Reviewing || report.ModeratorUserId != moderatorUserId)
            throw Conflict("Báo cáo phải được bạn nhận xử lý trước khi kết thúc.");

        report.Status = outcome;
        report.ResolutionCode = resolutionCode;
        report.ResolutionNote = resolutionNote;
        report.ResolvedAt = timeProvider.GetUtcNow();
        report.Version++;
        await SaveModerationAsync(report, moderatorUserId, $"content_report.{outcome}", cancellationToken);
        return ToAdminDetail(report);
    }

    private async Task<string?> ResolveSnapshotAsync(Guid userId, string contentType, Guid contentId, CancellationToken cancellationToken)
    {
        switch (contentType)
        {
            case ContentReportValues.LearningPath:
            {
                var path = await dbContext.LearningPaths.AsNoTracking().AsSplitQuery()
                    .Where(item => item.Id == contentId && item.UserId == userId &&
                        item.CareerGoal.UserId == userId && item.CareerGoal.DeletedAt == null &&
                        (item.Status == LearningPathValues.Active || item.Status == LearningPathValues.Completed))
                    .Include(item => item.Milestones).ThenInclude(item => item.Activities)
                    .SingleOrDefaultAsync(cancellationToken);
                if (path is null || path.Milestones.Count == 0) return null;
                var items = path.Milestones.OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                    .SelectMany(milestone => new object[]
                    {
                        new { kind = "milestone", milestone.Code, milestone.Title, milestone.Status }
                    }.Concat(milestone.Activities.OrderBy(item => item.SortOrder).ThenBy(item => item.Id)
                        .Select(activity => (object)new
                        {
                            kind = "activity", milestone = milestone.Code, activity.Type,
                            activity.Title, activity.Description, activity.CompetencyCode, activity.Status
                        })));
                return SerializeBounded(contentType, contentId, items);
            }
            case ContentReportValues.SkillProfile:
            {
                var profile = await skillProfiles.GetAsync(userId, cancellationToken);
                if (profile.ReportingId != contentId) return null;
                var items = profile.Competencies.Select(item => (object)new { kind = "competency", content = item })
                    .Concat(profile.WeaknessSignals.Select(item => (object)new { kind = "weakness", content = item }));
                return SerializeBounded(contentType, contentId, items);
            }
            case ContentReportValues.InterviewQuestion:
            {
                var content = await dbContext.InterviewQuestions.AsNoTracking()
                    .Where(item => item.Id == contentId && item.ReleasedAt != null && item.InterviewSession.UserId == userId)
                    .Select(item => item.Content).SingleOrDefaultAsync(cancellationToken);
                return content is null ? null : JsonSerializer.Serialize(new { content }, JsonOptions);
            }
            case ContentReportValues.InterviewAnswerEvaluation:
            {
                var evaluation = await dbContext.InterviewAnswers.AsNoTracking()
                    .Where(item => item.Id == contentId && item.UserId == userId &&
                        item.InterviewSession.UserId == userId &&
                        item.EvaluationStatus == InterviewAnswerEvaluationStates.Ready && item.Evaluation != null)
                    .Select(item => item.Evaluation).SingleOrDefaultAsync(cancellationToken);
                return evaluation is null ? null : SerializeJson(evaluation);
            }
            case ContentReportValues.InterviewReport:
            {
                var report = await dbContext.InterviewReports.AsNoTracking()
                    .Where(item => item.Id == contentId && item.UserId == userId && item.InterviewSession.UserId == userId)
                    .Select(item => new { item.Rubric, item.Strengths, item.Gaps, item.ActionPlan })
                    .SingleOrDefaultAsync(cancellationToken);
                return report is null ? null : JsonSerializer.Serialize(new
                {
                    rubric = ParseJson(report.Rubric),
                    strengths = ParseJson(report.Strengths),
                    gaps = ParseJson(report.Gaps),
                    actionPlan = ParseJson(report.ActionPlan)
                }, JsonOptions);
            }
            case ContentReportValues.ResumeAnalysis:
            {
                var result = await dbContext.ResumeAnalyses.AsNoTracking()
                    .Where(item => item.Id == contentId && item.UserId == userId &&
                        item.Resume.UserId == userId && item.Status == PracticeValues.Completed && item.Result != null)
                    .Select(item => item.Result).SingleOrDefaultAsync(cancellationToken);
                return result is null ? null : SerializeJson(result);
            }
            case ContentReportValues.ScenarioEvaluation:
            {
                var evaluation = await dbContext.ScenarioAttempts.AsNoTracking()
                    .Where(item => item.Id == contentId && item.Status == PracticeFeatureValues.Completed && item.UserId == userId && item.EvaluationJson != null)
                    .Select(item => item.EvaluationJson).SingleOrDefaultAsync(cancellationToken);
                return evaluation is null ? null : SerializeJson(evaluation);
            }
            case ContentReportValues.StarEvaluation:
            {
                var evaluation = await dbContext.StarAttempts.AsNoTracking()
                    .Where(item => item.Id == contentId && item.Status == PracticeFeatureValues.Completed && item.UserId == userId && item.EvaluationJson != null)
                    .Select(item => item.EvaluationJson).SingleOrDefaultAsync(cancellationToken);
                return evaluation is null ? null : SerializeJson(evaluation);
            }
            default:
                return null;
        }
    }

    private static string SerializeJson(string value) => JsonSerializer.Serialize(ParseJson(value), JsonOptions);

    private static string SerializeBounded(string contentType, Guid contentId, IEnumerable<object> source)
    {
        // Preserve complete JSON entries, never splice/truncate JSON or include raw source CV/answers.
        var items = new List<JsonElement>();
        var truncated = false;
        foreach (var item in source)
        {
            if (items.Count == 200) { truncated = true; break; }
            var entry = JsonSerializer.SerializeToElement(item, JsonOptions);
            items.Add(entry);
            var candidate = JsonSerializer.Serialize(new { contentType, contentId, items, truncated = true }, JsonOptions);
            if (candidate.Length <= ContentReportRules.MaximumSnapshotLength) continue;
            items.RemoveAt(items.Count - 1);
            truncated = true;
            break;
        }
        return JsonSerializer.Serialize(new { contentType, contentId, items, truncated }, JsonOptions);
    }

    private static JsonElement ParseJson(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private async Task SaveModerationAsync(
        ContentReport report,
        Guid moderatorUserId,
        string action,
        CancellationToken cancellationToken)
    {
        dbContext.AdminAuditEvents.Add(new AdminAuditEvent
        {
            Id = Guid.NewGuid(),
            AdminUserId = moderatorUserId,
            Action = action,
            TargetType = "content_report",
            TargetId = report.Id.ToString("N"),
            Reason = $"status={report.Status}",
            CreatedAt = timeProvider.GetUtcNow()
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw Conflict("Báo cáo đã được quản trị viên khác cập nhật.");
        }
    }

    private static ContentReportAdminItem ToAdminItem(ContentReport report) => new(
        report.Id, report.ReporterUserId, report.ContentType, report.ContentId, report.ReasonCode,
        report.Status, report.CreatedAt, report.ReviewedAt, report.ModeratorUserId);

    private static IQueryable<ContentReportAdminItem> ProjectAdmin(IQueryable<ContentReport> reports) =>
        reports.Select(item => new ContentReportAdminItem(
            item.Id, item.ReporterUserId, item.ContentType, item.ContentId, item.ReasonCode,
            item.Status, item.CreatedAt, item.ReviewedAt, item.ModeratorUserId));

    private static ContentReportAdminDetail ToAdminDetail(ContentReport report) => new(
        ToAdminItem(report), report.Description, report.ContentSnapshot, report.ResolutionCode,
        report.ResolutionNote, report.ResolvedAt);

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static string? OptionalFilter(string? value, IEnumerable<string> values, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = Normalize(value);
        return values.Contains(normalized, StringComparer.Ordinal) ? normalized : throw Validation(message);
    }

    private static bool IsSupportedType(string contentType) => ContentTypes.Contains(contentType, StringComparer.Ordinal);

    private static readonly string[] ContentTypes =
    [
        ContentReportValues.InterviewQuestion,
        ContentReportValues.InterviewAnswerEvaluation,
        ContentReportValues.InterviewReport,
        ContentReportValues.ResumeAnalysis,
        ContentReportValues.ScenarioEvaluation,
        ContentReportValues.StarEvaluation,
        ContentReportValues.LearningPath,
        ContentReportValues.SkillProfile
    ];

    private static BusinessException NotFound() =>
        new("RESOURCE_NOT_FOUND", "Không tìm thấy nội dung được báo cáo.", BusinessErrorKind.NotFound);

    private static BusinessException Validation(string message) =>
        new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);

    private static BusinessException Conflict(string message) =>
        new("CONTENT_REPORT_CONFLICT", message, BusinessErrorKind.Conflict);
}
