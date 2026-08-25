using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Business.Storage;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.Data.Privacy;

public sealed partial class PrivacyService(
    NexoraDbContext dbContext,
    IBillingService billingService,
    IStorageProvider storageProvider,
    TimeProvider timeProvider,
    ILogger<PrivacyService> logger) : IPrivacyService, IPrivacyJobProcessor
{
    private const string DeletionType = "account_deletion";
    private const int MaxAttempts = 3;

    public async Task<CoreDataExport> ExportAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking().Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Id == userId && item.DeletionRequestedAt == null && item.DeletedAt == null, cancellationToken)
            ?? throw NotFound();
        var resumes = await dbContext.Resumes.AsNoTracking().Include(item => item.StoredFile)
            .Where(item => item.UserId == userId).ToArrayAsync(cancellationToken);
        var jobDescriptions = await dbContext.JobDescriptions.AsNoTracking().Where(item => item.UserId == userId)
            .ToArrayAsync(cancellationToken);
        var analyses = await dbContext.ResumeAnalyses.AsNoTracking().Where(item => item.UserId == userId)
            .ToArrayAsync(cancellationToken);
        var sessions = await dbContext.InterviewSessions.AsNoTracking()
            .Include(item => item.Questions).Include(item => item.Answers).Include(item => item.Report)
            .Where(item => item.UserId == userId).ToArrayAsync(cancellationToken);

        return new CoreDataExport(
            timeProvider.GetUtcNow(),
            new ExportProfile(user.Id, user.Email ?? string.Empty, user.Profile?.DisplayName, user.CreatedAt),
            await billingService.GetSummaryAsync(userId, cancellationToken),
            resumes.OrderBy(item => item.CreatedAt).Select(item => new ExportResume(item.Id, item.StoredFile.FileName, item.StoredFile.ContentType,
                item.StoredFile.Size, item.Status, item.CreatedAt)).ToArray(),
            jobDescriptions.OrderBy(item => item.CreatedAt).Select(item => new ExportJobDescription(item.Id, item.Title, item.Content, item.CreatedAt)).ToArray(),
            analyses.OrderBy(item => item.CreatedAt).Select(item => new ExportAnalysis(item.Id, item.ResumeId, item.JobDescriptionId, item.Status,
                Parse(item.Result), item.CreatedAt)).ToArray(),
            sessions.OrderBy(item => item.CreatedAt).Select(item => new ExportInterview(MapInterview(item), item.Report is null ? null : MapReport(item.Report))).ToArray());
    }

    public async Task<DeletionRequestView> RequestDeletionAsync(Guid userId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var prior = await dbContext.DataPrivacyRequests.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.IdempotencyKey == key, cancellationToken);
        if (prior is not null) return Map(prior);

        var user = await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId && item.DeletedAt == null, cancellationToken)
            ?? throw NotFound();
        var now = timeProvider.GetUtcNow();
        user.DeletionRequestedAt ??= now;
        user.UpdatedAt = now;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        var activeTokens = await dbContext.RefreshTokens.Where(item => item.UserId == userId && item.RevokedAt == null).ToArrayAsync(cancellationToken);
        foreach (var token in activeTokens)
        {
            token.RevokedAt = now;
            token.ConcurrencyToken = Guid.NewGuid();
        }

        var request = new DataPrivacyRequest
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Type = DeletionType,
            Status = PrivacyValues.Queued,
            IdempotencyKey = key,
            RequestedAt = now,
            UpdatedAt = now,
            NextAttemptAt = now
        };
        dbContext.DataPrivacyRequests.Add(request);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(request);
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.AddMinutes(-10);
        var query = dbContext.DataPrivacyRequests
            .AsNoTracking().Where(item => item.Type == DeletionType &&
                (item.Status == PrivacyValues.Queued || item.Status == PrivacyValues.Processing));
        var candidates = dbContext.Database.IsNpgsql()
            ? await query.Where(item => (item.Status == PrivacyValues.Queued && item.NextAttemptAt <= now) ||
                    (item.Status == PrivacyValues.Processing && item.UpdatedAt <= staleBefore))
                .OrderBy(item => item.RequestedAt).Take(10).ToArrayAsync(cancellationToken)
            : (await query.ToArrayAsync(cancellationToken))
                .Where(item => (item.Status == PrivacyValues.Queued && item.NextAttemptAt <= now) ||
                    (item.Status == PrivacyValues.Processing && item.UpdatedAt <= staleBefore))
                .OrderBy(item => item.RequestedAt).Take(10).ToArray();
        var processed = 0;
        foreach (var candidate in candidates)
        {
            var claimed = await dbContext.DataPrivacyRequests.Where(item => item.Id == candidate.Id &&
                    item.Status == candidate.Status && item.UpdatedAt == candidate.UpdatedAt)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Status, PrivacyValues.Processing)
                    .SetProperty(item => item.UpdatedAt, now), cancellationToken);
            if (claimed == 0) continue;
            processed++;
            var request = await dbContext.DataPrivacyRequests.SingleAsync(item => item.Id == candidate.Id, cancellationToken);
            var started = Stopwatch.GetTimestamp();
            try
            {
                await ProcessDeletionAsync(request, cancellationToken);
                DeletionCompleted(logger, request.Id, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                dbContext.ChangeTracker.Clear();
                var failed = await dbContext.DataPrivacyRequests.SingleAsync(item => item.Id == request.Id, cancellationToken);
                failed.Attempts++;
                failed.UpdatedAt = timeProvider.GetUtcNow();
                failed.ErrorCode = "DELETION_FAILED";
                failed.Status = failed.Attempts >= MaxAttempts ? PrivacyValues.Failed : PrivacyValues.Queued;
                failed.NextAttemptAt = failed.Status == PrivacyValues.Queued
                    ? failed.UpdatedAt.AddMinutes(failed.Attempts)
                    : null;
                await dbContext.SaveChangesAsync(cancellationToken);
                DeletionFailed(logger, request.Id, failed.Attempts, failed.Status, exception.GetType().Name,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        return processed;
    }

    private async Task ProcessDeletionAsync(DataPrivacyRequest request, CancellationToken cancellationToken)
    {
        var storageKeys = await dbContext.StoredFiles.AsNoTracking().Where(item => item.UserId == request.UserId)
            .Select(item => item.StorageKey).ToArrayAsync(cancellationToken);
        foreach (var storageKey in storageKeys) await storageProvider.DeleteAsync(storageKey, cancellationToken);

        dbContext.ChangeTracker.Clear();
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var user = await dbContext.Users.SingleAsync(item => item.Id == request.UserId, cancellationToken);
        var sessions = await dbContext.InterviewSessions.Include(item => item.ReservationEvent)
            .Where(item => item.UserId == request.UserId).ToArrayAsync(cancellationToken);
        var sessionIds = sessions.Select(item => item.Id).ToArray();
        var resumeIds = await dbContext.Resumes.Where(item => item.UserId == request.UserId)
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        var jdIds = await dbContext.JobDescriptions.Where(item => item.UserId == request.UserId)
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        var analysisIds = await dbContext.ResumeAnalyses.Where(item => item.UserId == request.UserId)
            .Select(item => item.Id).ToArrayAsync(cancellationToken);
        var personalAggregateIds = sessionIds.Concat(resumeIds).Concat(jdIds).Concat(analysisIds).ToHashSet();

        var reservationSourceIds = sessions.Select(item => item.ReservationEventId.ToString("N")).ToArray();
        var finalized = await dbContext.UsageEvents.Where(item => reservationSourceIds.Contains(item.SourceId) &&
                (item.Action == BillingValues.Consume || item.Action == BillingValues.Void))
            .Select(item => item.SourceId).ToArrayAsync(cancellationToken);
        var finalizedSet = finalized.ToHashSet(StringComparer.Ordinal);
        var entitlementIds = sessions.Select(item => item.ReservationEvent.EntitlementId).Distinct().ToArray();
        var entitlements = await dbContext.Entitlements.Where(item => entitlementIds.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        foreach (var session in sessions.Where(item => !finalizedSet.Contains(item.ReservationEventId.ToString("N"))))
        {
            var reservation = session.ReservationEvent;
            var entitlement = entitlements[reservation.EntitlementId];
            entitlement.Reserved = Math.Max(0, entitlement.Reserved - reservation.Quantity);
            entitlement.UpdatedAt = now;
            entitlement.ConcurrencyToken = Guid.NewGuid();
            dbContext.UsageEvents.Add(new Nexora.Data.Billing.UsageEvent
            {
                Id = Guid.NewGuid(),
                UserId = request.UserId,
                EntitlementId = reservation.EntitlementId,
                Action = BillingValues.Void,
                Quantity = reservation.Quantity,
                SourceType = "reservation",
                SourceId = reservation.Id.ToString("N"),
                IdempotencyKey = $"void:{reservation.Id:N}",
                Reason = "Account deletion",
                CreatedAt = now
            });
        }

        dbContext.InterviewReports.RemoveRange(dbContext.InterviewReports.Where(item => item.UserId == request.UserId));
        dbContext.InterviewAnswers.RemoveRange(dbContext.InterviewAnswers.Where(item => item.UserId == request.UserId));
        dbContext.InterviewQuestions.RemoveRange(dbContext.InterviewQuestions.Where(item => sessionIds.Contains(item.InterviewSessionId)));
        dbContext.InterviewSessions.RemoveRange(sessions);
        dbContext.ResumeAnalyses.RemoveRange(dbContext.ResumeAnalyses.Where(item => item.UserId == request.UserId));
        dbContext.Resumes.RemoveRange(dbContext.Resumes.Where(item => item.UserId == request.UserId));
        dbContext.JobDescriptions.RemoveRange(dbContext.JobDescriptions.Where(item => item.UserId == request.UserId));
        dbContext.StoredFiles.RemoveRange(dbContext.StoredFiles.Where(item => item.UserId == request.UserId));
        dbContext.OutboxEvents.RemoveRange(dbContext.OutboxEvents.Where(item => personalAggregateIds.Contains(item.AggregateId)));
        dbContext.IdempotencyRecords.RemoveRange(dbContext.IdempotencyRecords.Where(item => item.ActorId == request.UserId));
        dbContext.RefreshTokens.RemoveRange(dbContext.RefreshTokens.Where(item => item.UserId == request.UserId));
        dbContext.UserProfiles.RemoveRange(dbContext.UserProfiles.Where(item => item.UserId == request.UserId));
        dbContext.Set<IdentityUserClaim<Guid>>().RemoveRange(dbContext.Set<IdentityUserClaim<Guid>>().Where(item => item.UserId == request.UserId));
        dbContext.Set<IdentityUserLogin<Guid>>().RemoveRange(dbContext.Set<IdentityUserLogin<Guid>>().Where(item => item.UserId == request.UserId));
        dbContext.Set<IdentityUserRole<Guid>>().RemoveRange(dbContext.Set<IdentityUserRole<Guid>>().Where(item => item.UserId == request.UserId));
        dbContext.Set<IdentityUserToken<Guid>>().RemoveRange(dbContext.Set<IdentityUserToken<Guid>>().Where(item => item.UserId == request.UserId));

        var anonymous = $"deleted-{user.Id:N}@invalid.local";
        user.Email = anonymous;
        user.NormalizedEmail = anonymous.ToUpperInvariant();
        user.UserName = anonymous;
        user.NormalizedUserName = anonymous.ToUpperInvariant();
        user.PhoneNumber = null;
        user.PasswordHash = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.ConcurrencyStamp = Guid.NewGuid().ToString("N");
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.DeletedAt = now;
        user.UpdatedAt = now;

        var trackedRequest = await dbContext.DataPrivacyRequests.SingleAsync(item => item.Id == request.Id, cancellationToken);
        trackedRequest.Status = PrivacyValues.Completed;
        trackedRequest.Attempts++;
        trackedRequest.ErrorCode = null;
        trackedRequest.NextAttemptAt = null;
        trackedRequest.CompletedAt = trackedRequest.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static InterviewView MapInterview(Nexora.Data.Practice.InterviewSession item) => new(
        item.Id, item.Status, item.Role, item.Seniority, item.InterviewType, item.Difficulty, item.Version,
        item.Questions.OrderBy(question => question.Sequence).Select(question => new QuestionView(
            question.Id, question.Sequence, question.Content, question.CreatedAt)).ToArray(),
        item.Answers.OrderBy(answer => answer.CreatedAt).Select(answer => new AnswerView(
            answer.Id, answer.QuestionId, answer.Content, answer.DurationSeconds, Parse(answer.Evaluation), answer.CreatedAt)).ToArray(),
        item.CreatedAt, item.UpdatedAt);

    private static ReportView MapReport(Nexora.Data.Practice.InterviewReport item) => new(
        item.Id, item.InterviewSessionId, item.OverallScore, Parse(item.Rubric) ?? EmptyJson(),
        Parse(item.Strengths) ?? EmptyJson(), Parse(item.Gaps) ?? EmptyJson(), Parse(item.ActionPlan) ?? EmptyJson(),
        item.Disclaimer, item.CreatedAt);

    private static JsonElement? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private static JsonElement EmptyJson()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
        ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
        : value.Trim();

    private static DeletionRequestView Map(DataPrivacyRequest request) =>
        new(request.Id, request.Status, request.Attempts, request.RequestedAt, request.CompletedAt);

    private static BusinessException NotFound() =>
        new("RESOURCE_NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);

    [LoggerMessage(LogLevel.Information, "Deletion request {RequestId} completed in {DurationMs} ms")]
    private static partial void DeletionCompleted(ILogger logger, Guid requestId, double durationMs);

    [LoggerMessage(LogLevel.Error,
        "Deletion request {RequestId} attempt {Attempt} ended as {Status} with {ExceptionType} in {DurationMs} ms")]
    private static partial void DeletionFailed(
        ILogger logger, Guid requestId, int attempt, string status, string exceptionType, double durationMs);
}
