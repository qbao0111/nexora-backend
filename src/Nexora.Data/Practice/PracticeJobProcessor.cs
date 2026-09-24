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

public sealed partial class PracticeJobProcessor(
    NexoraDbContext dbContext,
    ResumeExtractionJobHandler resumeExtractionJobHandler,
    ResumeAnalysisJobHandler resumeAnalysisJobHandler,
    ResumeStorageCleanupProcessor resumeStorageCleanupProcessor,
    InterviewStartJobHandler interviewStartJobHandler,
    InterviewQuestionPlanJobHandler interviewQuestionPlanJobHandler,
    InterviewAnswerEvaluationJobHandler interviewAnswerEvaluationJobHandler,
    InterviewReportJobHandler interviewReportJobHandler,
    TimeProvider timeProvider,
    ILogger<PracticeService> logger) : IPracticeJobProcessor
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
                    case "ResumeExtractionRequested": await resumeExtractionJobHandler.ProcessAsync(job, cancellationToken); break;
                    case "ResumeAnalysisRequested": await resumeAnalysisJobHandler.ProcessAsync(job, cancellationToken); break;
                    case "InterviewStartRequested": await interviewStartJobHandler.ProcessAsync(job, cancellationToken); break;
                    case "InterviewReportRequested": await interviewReportJobHandler.ProcessAsync(job, cancellationToken); break;
                    case "InterviewAnswerEvaluationRequested": await interviewAnswerEvaluationJobHandler.ProcessAsync(job, cancellationToken); break;
                    case "InterviewQuestionPlanRequested": await interviewQuestionPlanJobHandler.ProcessAsync(job, cancellationToken); break;
                }
                JobCompleted(logger, job.Id, job.Type, job.AggregateId, queueLagSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                if (job.Type == "ResumeExtractionRequested")
                    await resumeExtractionJobHandler.FailAsync(job, cancellationToken);
                else if (job.Type == "ResumeAnalysisRequested")
                    await resumeAnalysisJobHandler.FailAsync(job, cancellationToken);
                else if (job.Type == "InterviewStartRequested")
                    await interviewStartJobHandler.FailAsync(job, cancellationToken);
                else if (job.Type == "InterviewReportRequested")
                    await interviewReportJobHandler.FailAsync(job, cancellationToken);
                else if (job.Type == "InterviewAnswerEvaluationRequested")
                    await interviewAnswerEvaluationJobHandler.FailAsync(job, exception, cancellationToken);
                else if (job.Type == "InterviewQuestionPlanRequested")
                    await interviewQuestionPlanJobHandler.FailAsync(job, cancellationToken);
                JobFailed(logger, job.Id, job.Type, job.AggregateId, exception.GetType().Name,
                    queueLagSeconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }
        }
        return claimedCount + await resumeStorageCleanupProcessor.ProcessPendingAsync(cancellationToken);
    }

    [LoggerMessage(LogLevel.Information,
        "Job {JobId} ({JobType}/{AggregateId}) completed after {QueueLagSeconds} queue seconds in {DurationMs} ms")]
    private static partial void JobCompleted(
        ILogger logger, Guid jobId, string jobType, Guid aggregateId, double queueLagSeconds, double durationMs);

    [LoggerMessage(LogLevel.Error,
        "Job {JobId} ({JobType}/{AggregateId}) failed with {ExceptionType} after {QueueLagSeconds} queue seconds in {DurationMs} ms")]
    private static partial void JobFailed(
        ILogger logger, Guid jobId, string jobType, Guid aggregateId, string exceptionType, double queueLagSeconds, double durationMs);

}
