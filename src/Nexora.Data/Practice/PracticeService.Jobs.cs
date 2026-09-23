using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class PracticeService
{
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.AddMinutes(-10);
        var relevant = dbContext.OutboxEvents.AsNoTracking().Where(item =>
            item.Type == "ResumeExtractionRequested" || item.Type == "ResumeAnalysisRequested" ||
            item.Type == "InterviewStartRequested" || item.Type == "InterviewReportRequested" ||
            item.Type == "InterviewAnswerEvaluationRequested" || item.Type == "InterviewQuestionPlanRequested");
        var candidates = !dbContext.Database.IsNpgsql()
            ? (await relevant.ToArrayAsync(cancellationToken))
                .Where(item => item.Status == BillingValues.Pending ||
                    (item.Status == BillingValues.Processing && item.ProcessedAt <= staleBefore))
                .OrderBy(item => item.CreatedAt).Take(20).ToArray()
            : await relevant.Where(item => item.Status == BillingValues.Pending ||
                    (item.Status == BillingValues.Processing && item.ProcessedAt <= staleBefore))
                .OrderBy(item => item.CreatedAt).Take(20).ToArrayAsync(cancellationToken);
        var claimedCount = 0;
        foreach (var candidate in candidates)
        {
            var claimQuery = dbContext.OutboxEvents.Where(item => item.Id == candidate.Id && item.Status == candidate.Status);
            claimQuery = candidate.Status == BillingValues.Pending
                ? claimQuery.Where(item => item.ProcessedAt == null)
                : claimQuery.Where(item => item.ProcessedAt == candidate.ProcessedAt);
            var claimed = await claimQuery.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, BillingValues.Processing)
                .SetProperty(item => item.ProcessedAt, now), cancellationToken);
            if (claimed == 0) continue;

            claimedCount++;
            var job = await dbContext.OutboxEvents.SingleAsync(item => item.Id == candidate.Id, cancellationToken);
            var started = Stopwatch.GetTimestamp();
            var queueLagSeconds = Math.Max(0, (timeProvider.GetUtcNow() - job.CreatedAt).TotalSeconds);
            try
            {
                switch (job.Type)
                {
                    case "ResumeExtractionRequested": await ExtractResumeAsync(job, cancellationToken); break;
                    case "ResumeAnalysisRequested": await AnalyzeResumeAsync(job, cancellationToken); break;
                    case "InterviewStartRequested": await ActivateInterviewAsync(job, cancellationToken); break;
                    case "InterviewReportRequested": await BuildReportAsync(job, cancellationToken); break;
                    case "InterviewAnswerEvaluationRequested": await EvaluateInterviewAnswerAsync(job, cancellationToken); break;
                    case "InterviewQuestionPlanRequested": await PrepareInterviewQuestionsAsync(job, cancellationToken); break;
                }
                JobCompleted(logger, job.Id, job.Type, job.AggregateId, queueLagSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await FailJobAsync(job, exception, cancellationToken);
                JobFailed(logger, job.Id, job.Type, job.AggregateId, exception.GetType().Name,
                    queueLagSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        return claimedCount + await ProcessPendingResumeStorageCleanupAsync(cancellationToken);
    }

    private async Task<int> ProcessPendingResumeStorageCleanupAsync(CancellationToken cancellationToken)
    {
        const int batchSize = 20;
        var now = timeProvider.GetUtcNow();
        var pendingCleanup = dbContext.Resumes.AsNoTracking().Where(resume =>
            resume.DeletedAt != null && resume.StorageDeletedAt == null &&
            !dbContext.OutboxEvents.Any(job => job.AggregateId == resume.Id &&
                job.Type == "ResumeExtractionRequested" &&
                (job.Status == BillingValues.Pending || job.Status == BillingValues.Processing)));

        ResumeStorageCleanupCandidate[] candidates;
        if (dbContext.Database.IsNpgsql())
        {
            candidates = await pendingCleanup
                .Where(resume => resume.StorageDeleteNextAttemptAt == null || resume.StorageDeleteNextAttemptAt <= now)
                .OrderBy(resume => resume.StorageDeleteNextAttemptAt)
                .ThenBy(resume => resume.DeletedAt)
                .Take(batchSize)
                .Select(resume => new ResumeStorageCleanupCandidate(
                    resume.Id, resume.StorageDeleteAttempts, resume.StorageDeleteNextAttemptAt))
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            var rows = await pendingCleanup
                .Select(resume => new ResumeStorageCleanupCandidate(
                    resume.Id, resume.StorageDeleteAttempts, resume.StorageDeleteNextAttemptAt))
                .ToArrayAsync(cancellationToken);
            candidates = rows
                .Where(resume => resume.NextAttemptAt is null || resume.NextAttemptAt <= now)
                .Take(batchSize)
                .ToArray();
        }

        var attempted = 0;
        foreach (var candidate in candidates)
        {
            var leaseUntil = now.AddMinutes(5);
            var claim = dbContext.Resumes.Where(resume =>
                resume.Id == candidate.ResumeId && resume.DeletedAt != null && resume.StorageDeletedAt == null &&
                resume.StorageDeleteNextAttemptAt == candidate.NextAttemptAt &&
                !dbContext.OutboxEvents.Any(job => job.AggregateId == resume.Id &&
                    job.Type == "ResumeExtractionRequested" &&
                    (job.Status == BillingValues.Pending || job.Status == BillingValues.Processing)));
            var claimed = await claim.ExecuteUpdateAsync(setters => setters
                .SetProperty(resume => resume.StorageDeleteAttempts, resume => resume.StorageDeleteAttempts + 1)
                .SetProperty(resume => resume.StorageDeleteNextAttemptAt, leaseUntil), cancellationToken);
            if (claimed == 0) continue;

            attempted++;
            var attemptNumber = candidate.Attempts + 1;
            var storageKey = await dbContext.Resumes.AsNoTracking()
                .Where(resume => resume.Id == candidate.ResumeId)
                .Select(resume => resume.StoredFile.StorageKey)
                .SingleAsync(cancellationToken);
            try
            {
                await storageProvider.DeleteAsync(storageKey, cancellationToken);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                var retryAt = timeProvider.GetUtcNow().Add(ResumeStorageCleanupDelay(attemptNumber));
                await dbContext.Resumes
                    .Where(resume => resume.Id == candidate.ResumeId && resume.StorageDeletedAt == null &&
                                     resume.StorageDeleteNextAttemptAt == leaseUntil)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(resume => resume.StorageDeleteNextAttemptAt, retryAt), cancellationToken);
                ResumeStorageCleanupFailed(logger, candidate.ResumeId, attemptNumber);
                continue;
            }

            var deletedAt = timeProvider.GetUtcNow();
            await dbContext.Resumes
                .Where(resume => resume.Id == candidate.ResumeId && resume.DeletedAt != null &&
                                 resume.StorageDeletedAt == null && resume.StorageDeleteNextAttemptAt == leaseUntil)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(resume => resume.StorageDeletedAt, deletedAt)
                    .SetProperty(resume => resume.StorageDeleteNextAttemptAt, (DateTimeOffset?)null), cancellationToken);
        }

        return attempted;
    }

    private static TimeSpan ResumeStorageCleanupDelay(int attemptNumber)
    {
        var exponent = Math.Clamp(attemptNumber - 1, 0, 10);
        return TimeSpan.FromSeconds(Math.Min(21_600, 30 * (1 << exponent)));
    }

    private sealed record ResumeStorageCleanupCandidate(
        Guid ResumeId,
        int Attempts,
        DateTimeOffset? NextAttemptAt);

    private async Task FailJobAsync(OutboxEvent job, Exception exception, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var current = await dbContext.OutboxEvents.SingleAsync(item => item.Id == job.Id, cancellationToken);
        current.Status = PracticeValues.Failed;
        current.ProcessedAt = timeProvider.GetUtcNow();
        if (current.Type == "InterviewStartRequested")
        {
            var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            if (session.Status == PracticeValues.Starting)
            {
                var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
                var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
                FinalizeReservation(entitlement, reservation, BillingValues.Void, current.ProcessedAt.Value);
                session.Status = PracticeValues.Failed;
                EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, current.ProcessedAt.Value);
                session.Version++;
                session.UpdatedAt = current.ProcessedAt.Value;
            }
        }
        else if (current.Type == "ResumeExtractionRequested")
        {
            var resume = await dbContext.Resumes.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            await LockUserAsync(resume.UserId, cancellationToken);
            await dbContext.Entry(resume).ReloadAsync(cancellationToken);
            if (resume.DeletedAt is null)
            {
                resume.Status = PracticeValues.Failed;
                resume.UpdatedAt = current.ProcessedAt.Value;
                EnqueueResourceChanged(resume.UserId, "resume", resume.Id, resume.Status, resume.UpdatedAt);
            }
        }
        else if (current.Type == "ResumeAnalysisRequested")
        {
            var analysis = await dbContext.ResumeAnalyses.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            analysis.Status = PracticeValues.Failed;
            analysis.ErrorCode = "AI_PROCESSING_FAILED";
            analysis.UpdatedAt = current.ProcessedAt.Value;
            EnqueueResourceChanged(analysis.UserId, "resumeAnalysis", analysis.Id, analysis.Status, analysis.UpdatedAt);
            if (analysis.UsageReservationId.HasValue)
                await featureEntitlementService.VoidAsync(analysis.UserId, analysis.UsageReservationId.Value, cancellationToken);
        }
        else if (current.Type == "InterviewReportRequested")
        {
            var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
            var sourceId = session.Id.ToString("N");
            var credited = await dbContext.UsageEvents.AsNoTracking().AnyAsync(item => item.EntitlementId == reservation.EntitlementId &&
                item.Action == BillingValues.Adjustment && item.SourceId == sourceId, cancellationToken);
            if (!credited)
            {
                var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
                entitlement.Adjustment++;
                entitlement.UpdatedAt = current.ProcessedAt.Value;
                entitlement.ConcurrencyToken = Guid.NewGuid();
                dbContext.UsageEvents.Add(new UsageEvent
                {
                    Id = Guid.NewGuid(),
                    UserId = session.UserId,
                    EntitlementId = entitlement.Id,
                    Action = BillingValues.Adjustment,
                    Quantity = 1,
                    SourceType = "report_failure",
                    SourceId = sourceId,
                    IdempotencyKey = $"report-failure:{sourceId}",
                    Reason = "Terminal report generation failure credit",
                    CreatedAt = current.ProcessedAt.Value
                });
            }
        }
        else if (current.Type == "InterviewAnswerEvaluationRequested")
        {
            var answer = await dbContext.InterviewAnswers.SingleOrDefaultAsync(item => item.Id == current.AggregateId, cancellationToken);
            if (answer is not null && answer.EvaluationStatus != InterviewAnswerEvaluationStates.Ready)
            {
                answer.EvaluationStatus = InterviewAnswerEvaluationStates.Failed;
                answer.Evaluation = null;
                answer.EvaluationErrorCode = EvaluationErrorCode(exception);
                answer.EvaluationCompletedAt = current.ProcessedAt;
                EnqueueResourceChanged(answer.UserId, "interview", answer.InterviewSessionId, answer.EvaluationStatus, current.ProcessedAt.Value);
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private void MarkProcessed(OutboxEvent job) { job.Status = BillingValues.Processed; job.ProcessedAt = timeProvider.GetUtcNow(); }

    [LoggerMessage(LogLevel.Information,
        "Job {JobId} ({JobType}/{AggregateId}) completed after {QueueLagSeconds} queue seconds in {DurationMs} ms")]
    private static partial void JobCompleted(
        ILogger logger, Guid jobId, string jobType, Guid aggregateId, double queueLagSeconds, double durationMs);

    [LoggerMessage(LogLevel.Error,
        "Job {JobId} ({JobType}/{AggregateId}) failed with {ExceptionType} after {QueueLagSeconds} queue seconds in {DurationMs} ms")]
    private static partial void JobFailed(
        ILogger logger, Guid jobId, string jobType, Guid aggregateId, string exceptionType, double queueLagSeconds, double durationMs);

    [LoggerMessage(LogLevel.Warning,
        "Interview evaluation failed. purpose={Purpose} model={ModelVersion} answerId={AnswerId} interviewId={InterviewId} failureKind={FailureKind} requestId={RequestId}")]
    private static partial void EvaluationFailed(
        ILogger logger, string purpose, string modelVersion, Guid answerId, Guid interviewId, string failureKind, string requestId);

    [LoggerMessage(LogLevel.Warning,
        "Interview report recovered with deterministic fallback. purpose={Purpose} model={ModelVersion} interviewId={InterviewId} outcome=persisted_fallback correlationId={CorrelationId}")]
    private static partial void ReportFallbackPersisted(
        ILogger logger, string purpose, string modelVersion, Guid interviewId, string correlationId);

    [LoggerMessage(LogLevel.Information,
        "Resume {ResumeId} extracted with {PageCount} pages, {CharacterCount} chars, {WordCount} words, method {ExtractionMethod}, quality {QualityScore}, OCR fallback {OcrFallbackUsed}, warnings {Warnings}")]
    private static partial void ResumeExtractionMeasured(
        ILogger logger, Guid resumeId, int pageCount, int characterCount, int wordCount,
        string extractionMethod, double qualityScore, string warnings, bool ocrFallbackUsed);

    [LoggerMessage(LogLevel.Warning, "Resume {ResumeId} local extraction failed with {ExceptionType}; trying document fallback")]
    private static partial void LocalExtractionFailed(ILogger logger, Guid resumeId, string exceptionType);

    [LoggerMessage(LogLevel.Error, "Resume {ResumeId} storage integrity check failed: actualBytes={ActualBytes} expectedBytes={ExpectedBytes}")]
    private static partial void StorageIntegrityFailed(ILogger logger, Guid resumeId, long actualBytes, long expectedBytes);

    [LoggerMessage(LogLevel.Information, "Resume {ResumeId} entered document OCR fallback after {LocalQuality} local quality")]
    private static partial void OcrFallbackStarted(ILogger logger, Guid resumeId, string localQuality);

    [LoggerMessage(LogLevel.Warning,
        "Resume storage cleanup failed for resume {ResumeId}; retry scheduled after attempt {AttemptNumber}.")]
    private static partial void ResumeStorageCleanupFailed(ILogger logger, Guid resumeId, int attemptNumber);

}
