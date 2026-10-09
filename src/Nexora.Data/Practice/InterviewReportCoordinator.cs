using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

using static Nexora.Data.Practice.InterviewPersistence;

namespace Nexora.Data.Practice;

public sealed class InterviewReportCoordinator(NexoraDbContext dbContext, InterviewReadState readState, PaidJobQueueAdmission queueAdmission)
{
    internal async Task TryQueueReportIfReadyAsync(
        Guid interviewId,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        bool deferWhenFull = false)
    {
        var session = dbContext.Database.IsNpgsql()
            ? await dbContext.InterviewSessions.FromSqlInterpolated(
                    $"SELECT * FROM interview_sessions WHERE \"Id\" = {interviewId} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken)
            : await dbContext.InterviewSessions.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == interviewId, cancellationToken);
        if (session?.Status != PracticeValues.Completing)
            return;

        var answerStates = await dbContext.InterviewAnswers.AsNoTracking()
            .Where(item => item.InterviewSessionId == interviewId)
            .Select(item => item.EvaluationStatus)
            .ToArrayAsync(cancellationToken);
        if (answerStates.Length == 0 || answerStates.Any(state => state != InterviewAnswerEvaluationStates.Ready) ||
            await dbContext.InterviewReports.AnyAsync(item => item.InterviewSessionId == interviewId, cancellationToken) ||
            await readState.HasPendingReportJobAsync(interviewId, cancellationToken))
            return;

        var reportJob = Outbox("InterviewReportRequested", "interview", interviewId, now);
        dbContext.Add(reportJob);
        try { await queueAdmission.CheckAsync(cancellationToken); }
        catch (BusinessException exception) when (exception.Code == "AI_QUEUE_FULL" && deferWhenFull)
        {
            // Capacity is not an evaluation failure. The persisted completing
            // session + ready answers are the durable scheduling intent.
            dbContext.Entry(reportJob).State = EntityState.Detached;
        }
    }

    internal async Task ReconcileAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Never silently retry a failed report. Its explicit/free retry contract
        // remains authoritative. Only initial, not-yet-enqueued reports qualify.
        var ids = await dbContext.InterviewSessions.AsNoTracking()
            .Where(session => session.Status == PracticeValues.Completing &&
                session.Answers.Any() && session.Answers.All(answer => answer.EvaluationStatus == InterviewAnswerEvaluationStates.Ready) &&
                !dbContext.OutboxEvents.Any(job => job.Type == "InterviewReportRequested" && job.AggregateId == session.Id) &&
                !dbContext.InterviewReports.Any(report => report.InterviewSessionId == session.Id))
            .OrderBy(session => session.Id).Select(session => session.Id).Take(20).ToArrayAsync(cancellationToken);
        foreach (var id in ids)
        {
            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            await TryQueueReportIfReadyAsync(id, now, cancellationToken, deferWhenFull: true);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

}
