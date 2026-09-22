using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Common;
using Nexora.Business.Feedback;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Feedback;

public sealed class FeedbackService(
    NexoraDbContext dbContext,
    TimeProvider timeProvider) : IProductFeedbackService
{
    private const string PublicFallbackDisplayName = "Người dùng Nexora";

    public async Task<ProductFeedbackView?> GetCurrentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var feedback = await dbContext.ProductFeedbacks.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.DeletedAt == null, cancellationToken);
        return feedback is null ? null : Map(feedback);
    }

    public async Task<ProductFeedbackView> UpsertAsync(
        Guid userId,
        FeedbackWriteCommand command,
        CancellationToken cancellationToken)
    {
        Validate(command);
        var comment = NormalizeComment(command.Comment);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken);
        await LockUserAsync(userId, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var feedback = await dbContext.ProductFeedbacks
            .SingleOrDefaultAsync(item => item.UserId == userId && item.DeletedAt == null, cancellationToken);
        var isNew = feedback is null;
        if (feedback is null)
        {
            feedback = new ProductFeedback
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CreatedAt = now
            };
            dbContext.ProductFeedbacks.Add(feedback);
        }

        var changed = isNew || feedback.Rating != command.Rating ||
            !string.Equals(feedback.Comment, comment, StringComparison.Ordinal) ||
            feedback.Consent != command.Consent;
        if (!changed)
        {
            await transaction.CommitAsync(cancellationToken);
            return Map(feedback);
        }

        feedback.Rating = command.Rating;
        feedback.Comment = comment;
        feedback.Consent = command.Consent;
        feedback.Status = FeedbackValues.Pending;
        feedback.Featured = false;
        feedback.ModeratedAt = null;
        feedback.ModeratedByUserId = null;
        feedback.PublishedAt = null;
        feedback.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Map(feedback);
    }

    public async Task DeleteCurrentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var feedback = await dbContext.ProductFeedbacks
            .SingleOrDefaultAsync(item => item.UserId == userId && item.DeletedAt == null, cancellationToken);
        if (feedback is null) return;

        var now = timeProvider.GetUtcNow();
        feedback.DeletedAt = now;
        feedback.UpdatedAt = now;
        feedback.Featured = false;
        feedback.Consent = false;
        feedback.PublishedAt = null;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<PublicFeedbackView>> GetPublicAsync(
        int limit,
        CancellationToken cancellationToken)
    {
        limit = Math.Clamp(limit <= 0 ? FeedbackRules.DefaultPublicLimit : limit, 1, FeedbackRules.MaximumPublicLimit);
        var query = dbContext.ProductFeedbacks.AsNoTracking()
            .Where(item => item.DeletedAt == null &&
                          item.Status == FeedbackValues.Approved &&
                          item.Consent &&
                          item.Comment != null &&
                          item.Comment.Trim() != string.Empty &&
                          item.User.IsActive &&
                          item.User.DeletionRequestedAt == null &&
                          item.User.DeletedAt == null)
            .Select(item => new PublicFeedbackRow(
                item.Id,
                item.User.Profile == null ? null : item.User.Profile.DisplayName,
                item.Rating,
                item.Comment!,
                item.Featured,
                item.CreatedAt,
                item.PublishedAt ?? item.UpdatedAt));
        var rows = dbContext.Database.IsNpgsql()
            ? await query
                .OrderByDescending(item => item.Featured)
                .ThenByDescending(item => item.PublishedAt)
                .ThenByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(limit)
                .ToArrayAsync(cancellationToken)
            : (await query.ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.Featured)
                .ThenByDescending(item => item.PublishedAt)
                .ThenByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(limit)
                .ToArray();

        return rows.Select(item => new PublicFeedbackView(
            item.Id,
            string.IsNullOrWhiteSpace(item.DisplayName) ? PublicFallbackDisplayName : item.DisplayName.Trim(),
            item.Rating,
            item.Comment.Trim(),
            item.PublishedAt)).ToArray();
    }

    public async Task<ProductFeedbackAdminPage> GetAdminPageAsync(
        ProductFeedbackAdminQuery query,
        CancellationToken cancellationToken)
    {
        var pageSize = query.PageSize is < 1 or > FeedbackRules.MaximumAdminPageSize ? 20 : query.PageSize;
        var feedback = dbContext.ProductFeedbacks.AsNoTracking().AsQueryable();
        if (!query.IncludeDeleted)
            feedback = feedback.Where(item => item.DeletedAt == null);

        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = query.Status.Trim().ToLowerInvariant();
            if (status is not (FeedbackValues.Pending or FeedbackValues.Approved or FeedbackValues.Rejected))
                throw Validation("Trạng thái feedback không hợp lệ.");
            feedback = feedback.Where(item => item.Status == status);
        }

        if (query.Featured.HasValue)
            feedback = feedback.Where(item => item.Featured == query.Featured.Value);
        if (query.Consent.HasValue)
            feedback = feedback.Where(item => item.Consent == query.Consent.Value);
        if (query.Rating is < 1 or > 5)
            throw Validation("Rating feedback không hợp lệ.");
        if (query.Rating.HasValue)
            feedback = feedback.Where(item => item.Rating == query.Rating.Value);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = $"%{EscapeLikePattern(query.Search.Trim())}%";
            feedback = dbContext.Database.IsNpgsql()
                ? feedback.Where(item =>
                    EF.Functions.ILike(item.User.Email!, search, "\\") ||
                    (item.User.Profile != null && item.User.Profile.DisplayName != null &&
                     EF.Functions.ILike(item.User.Profile.DisplayName, search, "\\")) ||
                    EF.Functions.ILike(item.Comment!, search, "\\"))
                : feedback.Where(item =>
                    EF.Functions.Like(item.User.Email!, search, "\\") ||
                    (item.User.Profile != null && item.User.Profile.DisplayName != null &&
                     EF.Functions.Like(item.User.Profile.DisplayName, search, "\\")) ||
                    EF.Functions.Like(item.Comment!, search, "\\"));
        }

        (DateTimeOffset CreatedAt, Guid Id)? cursor =
            string.IsNullOrWhiteSpace(query.Cursor) ? null : DecodeCursor(query.Cursor);
        ProductFeedbackAdminView[] rows;
        if (dbContext.Database.IsNpgsql())
        {
            if (query.From.HasValue)
                feedback = feedback.Where(item => item.CreatedAt >= query.From.Value);
            if (query.To.HasValue)
                feedback = feedback.Where(item => item.CreatedAt <= query.To.Value);
            if (cursor.HasValue)
                feedback = feedback.Where(item => item.CreatedAt < cursor.Value.CreatedAt ||
                                                   (item.CreatedAt == cursor.Value.CreatedAt && item.Id.CompareTo(cursor.Value.Id) < 0));
            rows = await ProjectAdmin(feedback
                    .OrderByDescending(item => item.CreatedAt)
                    .ThenByDescending(item => item.Id))
                .Take(pageSize + 1)
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            var materialized = await ProjectAdmin(feedback).ToArrayAsync(cancellationToken);
            rows = materialized
                .Where(item => !query.From.HasValue || item.CreatedAt >= query.From.Value)
                .Where(item => !query.To.HasValue || item.CreatedAt <= query.To.Value)
                .Where(item => !cursor.HasValue || item.CreatedAt < cursor.Value.CreatedAt ||
                    (item.CreatedAt == cursor.Value.CreatedAt && item.Id.CompareTo(cursor.Value.Id) < 0))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(pageSize + 1)
                .ToArray();
        }
        var items = rows.Take(pageSize).ToArray();
        var nextCursor = rows.Length > pageSize && items.Length > 0
            ? EncodeCursor(items[^1].CreatedAt, items[^1].Id)
            : null;
        return new ProductFeedbackAdminPage(items, nextCursor, pageSize);
    }

    public async Task<ProductFeedbackSummary> GetSummaryAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.ProductFeedbacks.AsNoTracking()
            .Where(item => item.DeletedAt == null)
            .Select(item => new FeedbackSummaryRow(
                item.Status,
                item.Featured,
                item.Rating,
                item.PublishedAt != null && item.Consent && item.Comment != null && item.Comment.Trim() != string.Empty))
            .ToArrayAsync(cancellationToken);
        return new ProductFeedbackSummary(
            rows.Length,
            rows.Count(item => item.Status == FeedbackValues.Pending),
            rows.Count(item => item.Status == FeedbackValues.Approved),
            rows.Count(item => item.Status == FeedbackValues.Rejected),
            rows.Count(item => item.Published),
            rows.Count(item => item.Featured),
            rows.Length == 0 ? null : rows.Average(item => (double)item.Rating),
            Enumerable.Range(1, 5).ToDictionary(
                rating => rating,
                rating => rows.Count(item => item.Rating == rating)));
    }

    public Task<ProductFeedbackAdminView> ApproveAsync(
        Guid adminUserId,
        Guid feedbackId,
        string? reason,
        CancellationToken cancellationToken) =>
        SetModerationAsync(adminUserId, feedbackId, FeedbackValues.Approved, null, reason, cancellationToken);

    public Task<ProductFeedbackAdminView> RejectAsync(
        Guid adminUserId,
        Guid feedbackId,
        string? reason,
        CancellationToken cancellationToken) =>
        SetModerationAsync(adminUserId, feedbackId, FeedbackValues.Rejected, false, reason, cancellationToken);

    public Task<ProductFeedbackAdminView> FeatureAsync(
        Guid adminUserId,
        Guid feedbackId,
        string? reason,
        CancellationToken cancellationToken) =>
        SetModerationAsync(adminUserId, feedbackId, null, true, reason, cancellationToken);

    public Task<ProductFeedbackAdminView> UnfeatureAsync(
        Guid adminUserId,
        Guid feedbackId,
        string? reason,
        CancellationToken cancellationToken) =>
        SetModerationAsync(adminUserId, feedbackId, null, false, reason, cancellationToken);

    private async Task<ProductFeedbackAdminView> SetModerationAsync(
        Guid adminUserId,
        Guid feedbackId,
        string? status,
        bool? featured,
        string? reason,
        CancellationToken cancellationToken)
    {
        var feedback = await dbContext.ProductFeedbacks
            .Include(item => item.User)
            .ThenInclude(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Id == feedbackId, cancellationToken)
            ?? throw new BusinessException("FEEDBACK_NOT_FOUND", "Không tìm thấy feedback.", BusinessErrorKind.NotFound);
        if (feedback.DeletedAt is not null)
            throw new BusinessException("FEEDBACK_DELETED", "Feedback đã bị xóa.", BusinessErrorKind.Conflict);

        var action = status is null ? featured == true ? "feature" : "unfeature" : status;
        if (featured == true &&
            (feedback.Status != FeedbackValues.Approved || !feedback.Consent || string.IsNullOrWhiteSpace(feedback.Comment)))
            throw new BusinessException("FEEDBACK_NOT_PUBLISHABLE", "Feedback chưa đủ điều kiện để nổi bật.", BusinessErrorKind.Conflict);

        var now = timeProvider.GetUtcNow();
        if (status == FeedbackValues.Approved)
        {
            feedback.Status = FeedbackValues.Approved;
            feedback.ModeratedAt = now;
            feedback.ModeratedByUserId = adminUserId;
            feedback.PublishedAt = feedback.Consent && !string.IsNullOrWhiteSpace(feedback.Comment) ? now : null;
        }
        else if (status == FeedbackValues.Rejected)
        {
            feedback.Status = FeedbackValues.Rejected;
            feedback.ModeratedAt = now;
            feedback.ModeratedByUserId = adminUserId;
            feedback.PublishedAt = null;
            feedback.Featured = false;
        }

        if (featured.HasValue)
            feedback.Featured = featured.Value;
        if (featured is false)
            feedback.PublishedAt = feedback.Status == FeedbackValues.Approved && feedback.Consent && !string.IsNullOrWhiteSpace(feedback.Comment)
                ? feedback.PublishedAt
                : null;
        feedback.UpdatedAt = now;
        dbContext.AdminAuditEvents.Add(new AdminAuditEvent
        {
            Id = Guid.NewGuid(),
            AdminUserId = adminUserId,
            Action = $"feedback.{action}",
            TargetType = "product_feedback",
            TargetId = feedback.Id.ToString("N"),
            Reason = NormalizeReason(reason, action),
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapAdmin(feedback);
    }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null)
            throw new BusinessException("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    }

    private static IQueryable<ProductFeedbackAdminView> ProjectAdmin(IQueryable<ProductFeedback> feedback) =>
        feedback.Select(item => new ProductFeedbackAdminView(
            item.Id,
            item.UserId,
            item.User.Email ?? string.Empty,
            item.User.Profile == null ? null : item.User.Profile.DisplayName,
            item.Rating,
            item.Comment,
            item.Consent,
            item.Status,
            item.Featured,
            item.CreatedAt,
            item.UpdatedAt,
            item.ModeratedAt,
            item.PublishedAt,
            item.DeletedAt,
            item.ModeratedByUserId));

    private static ProductFeedbackView Map(ProductFeedback feedback) => new(
        feedback.Id,
        feedback.Rating,
        feedback.Comment,
        feedback.Consent,
        feedback.Status,
        feedback.Featured,
        feedback.CreatedAt,
        feedback.UpdatedAt,
        feedback.ModeratedAt,
        feedback.PublishedAt,
        feedback.DeletedAt,
        feedback.ModeratedByUserId);

    private static ProductFeedbackAdminView MapAdmin(ProductFeedback feedback) => new(
        feedback.Id,
        feedback.UserId,
        feedback.User.Email ?? string.Empty,
        feedback.User.Profile?.DisplayName,
        feedback.Rating,
        feedback.Comment,
        feedback.Consent,
        feedback.Status,
        feedback.Featured,
        feedback.CreatedAt,
        feedback.UpdatedAt,
        feedback.ModeratedAt,
        feedback.PublishedAt,
        feedback.DeletedAt,
        feedback.ModeratedByUserId);

    private static void Validate(FeedbackWriteCommand command)
    {
        if (command.Rating is < 1 or > 5)
            throw new BusinessException("FEEDBACK_RATING_INVALID", "Đánh giá phải từ 1 đến 5.", BusinessErrorKind.Validation);
        if (command.Comment?.Trim().Length > FeedbackRules.MaximumCommentLength)
            throw new BusinessException("FEEDBACK_COMMENT_TOO_LONG", "Nhận xét tối đa 1000 ký tự.", BusinessErrorKind.Validation);
    }

    private static string? NormalizeComment(string? comment) =>
        string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();

    private static string NormalizeReason(string? reason, string action)
    {
        var normalized = string.IsNullOrWhiteSpace(reason) ? $"Feedback {action}" : reason.Trim();
        if (normalized.Length > 500)
            throw new BusinessException("VALIDATION_ERROR", "Lý do tối đa 500 ký tự.", BusinessErrorKind.Validation);
        return normalized;
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    private static string EncodeCursor(DateTimeOffset createdAt, Guid id)
    {
        var value = $"{createdAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static (DateTimeOffset CreatedAt, Guid Id) DecodeCursor(string value)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(normalized.Length + ((4 - normalized.Length % 4) % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(normalized)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) ||
                !Guid.TryParseExact(parts[1], "N", out var id)) throw new FormatException();
            return (new DateTimeOffset(ticks, TimeSpan.Zero), id);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            throw new BusinessException("VALIDATION_ERROR", "Cursor feedback không hợp lệ.", BusinessErrorKind.Validation);
        }
    }

    private static BusinessException Validation(string message) =>
        new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);

    private sealed record PublicFeedbackRow(
        Guid Id,
        string? DisplayName,
        int Rating,
        string Comment,
        bool Featured,
        DateTimeOffset CreatedAt,
        DateTimeOffset PublishedAt);
    private sealed record FeedbackSummaryRow(string Status, bool Featured, int Rating, bool Published);
}
