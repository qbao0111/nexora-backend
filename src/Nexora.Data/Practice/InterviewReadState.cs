using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

using static Nexora.Data.Practice.InterviewErrors;

namespace Nexora.Data.Practice;

public sealed class InterviewReadState(NexoraDbContext dbContext, IFeatureEntitlementService featureEntitlementService)
{
    internal Task<bool> HasPendingReportJobAsync(Guid interviewId, CancellationToken cancellationToken) =>
        dbContext.OutboxEvents.AnyAsync(item => item.AggregateId == interviewId &&
            item.Type == "InterviewReportRequested" &&
            (item.Status == BillingValues.Pending || item.Status == BillingValues.Processing), cancellationToken);

    internal Task<bool> HasPendingQuestionPlanJobAsync(Guid interviewId, CancellationToken cancellationToken) =>
        dbContext.OutboxEvents.AnyAsync(item => item.AggregateId == interviewId &&
            item.Type == "InterviewQuestionPlanRequested" &&
            (item.Status == BillingValues.Pending || item.Status == BillingValues.Processing), cancellationToken);

    internal async Task<(Guid Id, string Status, DateTimeOffset CreatedAt)?> GetLatestQuestionPlanJobAsync(
        Guid interviewId,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.OutboxEvents.AsNoTracking()
            .Where(item => item.AggregateId == interviewId && item.Type == "InterviewQuestionPlanRequested")
            .Select(item => new { item.Id, item.Status, item.CreatedAt })
            .ToArrayAsync(cancellationToken);
        var latest = rows
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();
        return latest is null ? null : (latest.Id, latest.Status, latest.CreatedAt);
    }

    internal async Task<string> GetQuestionPreparationStateAsync(
        Guid userId,
        InterviewSession session,
        CancellationToken cancellationToken)
    {
        if (session.Status != PracticeValues.Active)
            return InterviewQuestionPreparationStates.Processing;

        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);
        var released = session.Questions.Where(item => item.ReleasedAt is not null).ToArray();
        var hasUnansweredReleasedQuestion = released.Any(item =>
            session.Answers.All(answer => answer.QuestionId != item.Id));
        if (hasUnansweredReleasedQuestion || session.Questions.Count >= questionLimit)
            return InterviewQuestionPreparationStates.Ready;

        if (await HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
            return InterviewQuestionPreparationStates.Processing;

        var latestPlan = await GetLatestQuestionPlanJobAsync(session.Id, cancellationToken);
        return latestPlan?.Status == BillingValues.Failed
            ? InterviewQuestionPreparationStates.Failed
            : InterviewQuestionPreparationStates.Processing;
    }

    internal static int PreparationLimit(int questionLimit) =>
        Math.Min(InterviewQuestionValues.MaxQuestionsPerSession,
            Math.Max(InterviewQuestionValues.FreeQuestionLimit, questionLimit));

    internal static InterviewEvaluationProgress BuildEvaluationProgress(IEnumerable<InterviewAnswer> answers)
    {
        var states = answers.Select(item => item.EvaluationStatus).ToArray();
        return new(
            states.Length,
            states.Count(state => state == InterviewAnswerEvaluationStates.Queued),
            states.Count(state => state == InterviewAnswerEvaluationStates.Processing),
            states.Count(state => state == InterviewAnswerEvaluationStates.Ready),
            states.Count(state => state == InterviewAnswerEvaluationStates.Failed));
    }

    internal static string GetResultState(
        string status,
        string reportState,
        InterviewEvaluationProgress progress) =>
        status == PracticeValues.Active
                ? InterviewResultStates.Collecting
                : progress.Failed > 0 || reportState == InterviewReportStates.Failed
                    ? InterviewResultStates.Failed
                : status == PracticeValues.Completed && reportState == InterviewReportStates.Ready
                    ? InterviewResultStates.Ready
                    : InterviewResultStates.Processing;

    internal async Task<string> GetReportStateAsync(
        Guid interviewId,
        string interviewStatus,
        CancellationToken cancellationToken)
    {
        if (await dbContext.InterviewReports.AsNoTracking()
                .AnyAsync(item => item.InterviewSessionId == interviewId, cancellationToken))
            return InterviewReportStates.Ready;

        var latestJob = (await dbContext.OutboxEvents.AsNoTracking()
                .Where(item => item.AggregateId == interviewId && item.Type == "InterviewReportRequested")
                .Select(item => new { item.Id, item.Status, item.CreatedAt })
                .ToArrayAsync(cancellationToken))
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .FirstOrDefault();
        if (latestJob?.Status == BillingValues.Failed)
            return InterviewReportStates.Failed;
        if (string.Equals(interviewStatus, PracticeValues.Completing, StringComparison.Ordinal) &&
            latestJob?.Status is BillingValues.Pending or BillingValues.Processing)
            return InterviewReportStates.Processing;
        return InterviewReportStates.None;
    }

    internal async Task<InterviewContinuationView?> BuildContinuationAsync(
        Guid userId,
        InterviewSession session,
        CancellationToken cancellationToken)
    {
        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);
        return BuildContinuation(session, questionLimit);
    }

    internal static InterviewContinuationView? BuildContinuation(InterviewSession session, int questionLimit)
    {
        if (session.Status != PracticeValues.Active)
            return null;

        var issued = session.Questions.Where(item => item.Sequence <= questionLimit && item.ReleasedAt is not null).ToArray();
        var questions = issued.Length;
        var answered = session.Answers.Count(answer => !string.IsNullOrWhiteSpace(answer.Content) &&
            issued.Any(question => question.Id == answer.QuestionId));
        var canFinishNow = answered >= MinimumReportAnswers;
        var allIssuedQuestionsAnswered = questions > 0 && answered >= questions;
        if (questions >= questionLimit && allIssuedQuestionsAnswered)
        {
            var isFreeCap = questionLimit <= InterviewQuestionValues.FreeQuestionLimit;
            return new InterviewContinuationView(
                isFreeCap ? InterviewContinuationValues.UpgradeRequired : InterviewContinuationValues.MaxQuestionsReached,
                canFinishNow,
                isFreeCap);
        }

        return new InterviewContinuationView(InterviewContinuationValues.InProgress, canFinishNow, false);
    }

    internal async Task<int> GetQuestionLimitAsync(Guid userId, CancellationToken cancellationToken)
    {
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.InterviewQuestionLimit, cancellationToken);
        if (!access.Enabled)
            return InterviewQuestionValues.FreeQuestionLimit;
        if (access.Unlimited)
            return InterviewQuestionValues.MaxQuestionsPerSession;
        if (access.Limit is null or <= 0)
            return InterviewQuestionValues.FreeQuestionLimit;
        return Math.Clamp(access.Limit.Value,
            InterviewQuestionValues.FreeQuestionLimit,
            InterviewQuestionValues.MaxQuestionsPerSession);
    }

}
