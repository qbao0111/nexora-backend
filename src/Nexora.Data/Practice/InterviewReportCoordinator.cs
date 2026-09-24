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

public sealed class InterviewReportCoordinator(NexoraDbContext dbContext, InterviewReadState readState)
{
    internal async Task TryQueueReportIfReadyAsync(
        Guid interviewId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
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

        dbContext.Add(Outbox("InterviewReportRequested", "interview", interviewId, now));
    }


}
