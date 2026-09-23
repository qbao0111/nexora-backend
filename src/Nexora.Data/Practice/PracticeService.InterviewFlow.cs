using Microsoft.EntityFrameworkCore;
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
    public async Task<InterviewView> ContinueInterviewAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        // A concurrent replay can wait on the interview row lock after its
        // initial idempotency reads. Re-check after acquiring the lock so it
        // replays the committed continuation instead of creating another
        // idempotency record or question-plan job.
        prior = await FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active) throw InvalidState();
        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);

        var released = session.Questions.Where(item => item.ReleasedAt is not null).OrderBy(item => item.Sequence).ToArray();
        if (released.Length < InterviewQuestionValues.FreeQuestionLimit ||
            released.Count(item => session.Answers.Any(answer => answer.QuestionId == item.Id)) < InterviewQuestionValues.FreeQuestionLimit)
            throw InvalidState();

        var existingPending = session.Questions
            .OrderBy(item => item.Sequence)
            .FirstOrDefault(item => item.ReleasedAt is not null && session.Answers.All(answer => answer.QuestionId != item.Id));
        if (existingPending is not null)
        {
            var replayAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.continue", key, fingerprint, session.Id, replayAt));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, interviewId, cancellationToken);
        }

        if (session.Questions.Count >= questionLimit)
        {
            if (questionLimit <= InterviewQuestionValues.FreeQuestionLimit)
                throw InterviewUpgradeRequired();
            throw InterviewLimitReached();
        }
        if (await HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
        {
            var pendingAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.continue", key, fingerprint, session.Id, pendingAt));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, interviewId, cancellationToken);
        }
        var now = timeProvider.GetUtcNow();
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(
            Idempotency(userId, "interview.continue", key, fingerprint, session.Id, now),
            Outbox("InterviewQuestionPlanRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, interviewId, cancellationToken);
    }

    public async Task<InterviewView> RetryQuestionPreparationAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
            return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }

        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        // A concurrent replay can wait on the interview row lock after its
        // initial idempotency reads. Re-check after acquiring the lock so it
        // replays the committed retry instead of reporting a transient state
        // conflict.
        prior = await FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active)
            throw InvalidState();

        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);
        if (questionLimit <= InterviewQuestionValues.FreeQuestionLimit)
            throw InterviewUpgradeRequired();

        var released = session.Questions.Where(item => item.ReleasedAt is not null).ToArray();
        if (released.Any(item => session.Answers.All(answer => answer.QuestionId != item.Id)))
            throw new BusinessException(
                "INTERVIEW_QUESTION_ALREADY_READY",
                "Câu hỏi tiếp theo đã sẵn sàng.",
                BusinessErrorKind.Conflict);
        if (session.Questions.Count >= questionLimit)
            throw InterviewLimitReached();
        if (await HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
            throw new BusinessException(
                "INTERVIEW_QUESTION_PREPARATION_IN_PROGRESS",
                "Câu hỏi tiếp theo đang được chuẩn bị.",
                BusinessErrorKind.Conflict);

        var latestPlan = await GetLatestQuestionPlanJobAsync(session.Id, cancellationToken);
        if (latestPlan?.Status != BillingValues.Failed)
            throw new BusinessException(
                "INTERVIEW_QUESTION_PREPARATION_NOT_FAILED",
                "Chưa có lỗi chuẩn bị câu hỏi cần thử lại.",
                BusinessErrorKind.Conflict);

        var now = timeProvider.GetUtcNow();
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(
            Idempotency(userId, "interview.questions.retry", key, fingerprint, session.Id, now),
            Outbox("InterviewQuestionPlanRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, interviewId, cancellationToken);
    }

    public async Task<InterviewView> CompleteInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.complete", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await FindIdempotentAsync(userId, "interview.complete", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status == PracticeValues.Completed)
        {
            dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, timeProvider.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, session.Id, cancellationToken);
        }
        if (session.Status == PracticeValues.Completing)
        {
            var retryAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, retryAt));
            await TryQueueReportIfReadyAsync(session.Id, retryAt, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, session.Id, cancellationToken);
        }
        var answeredCount = session.Answers.Count(answer => !string.IsNullOrWhiteSpace(answer.Content));
        if (session.Status != PracticeValues.Active || session.Questions.Count == 0 ||
            answeredCount < MinimumReportAnswers || answeredCount != session.Answers.Count)
            throw InvalidState();
        var now = timeProvider.GetUtcNow();
        session.Status = PracticeValues.Completing;
        session.Version++;
        session.UpdatedAt = now;
        dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, session.Id, cancellationToken);
    }

    public async Task<InterviewView> RetryReportAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.report.retry", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await FindIdempotentAsync(userId, "interview.report.retry", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status is not PracticeValues.Completing and not PracticeValues.Completed)
            throw InvalidState();

        var now = timeProvider.GetUtcNow();
        dbContext.Add(Idempotency(userId, "interview.report.retry", key, fingerprint, session.Id, now));
        if (session.Status == PracticeValues.Completing)
            await TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, session.Id, cancellationToken);
    }

    public async Task<InterviewView> RetryResultsAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await FindIdempotentAsync(userId, "interview.results.retry", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await FindIdempotentAsync(userId, "interview.results.retry", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        if (session.Status is not PracticeValues.Completing and not PracticeValues.Completed)
            throw InvalidState();

        var now = timeProvider.GetUtcNow();
        var failedAnswers = session.Answers.Where(item => item.EvaluationStatus == InterviewAnswerEvaluationStates.Failed).ToArray();
        foreach (var answer in failedAnswers)
        {
            answer.EvaluationStatus = InterviewAnswerEvaluationStates.Queued;
            answer.EvaluationErrorCode = null;
            answer.EvaluationCompletedAt = null;
            if (!await dbContext.OutboxEvents.AnyAsync(item => item.AggregateId == answer.Id &&
                    item.Type == "InterviewAnswerEvaluationRequested" &&
                    (item.Status == BillingValues.Pending || item.Status == BillingValues.Processing), cancellationToken))
                dbContext.Add(Outbox("InterviewAnswerEvaluationRequested", "interviewAnswer", answer.Id, now));
        }

        if (session.Status == PracticeValues.Completing)
        {
            await TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        }
        dbContext.Add(Idempotency(userId, "interview.results.retry", key, fingerprint, session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await GetInterviewAsync(userId, session.Id, cancellationToken);
    }

    public async Task<ReportView> GetReportAsync(Guid userId, Guid interviewId, CancellationToken cancellationToken)
    {
        var report = await dbContext.InterviewReports.AsNoTracking()
            .Include(item => item.InterviewSession).ThenInclude(item => item.Answers)
            .Include(item => item.InterviewSession).ThenInclude(item => item.Questions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(item => item.InterviewSessionId == interviewId && item.UserId == userId, cancellationToken);
        if (report is not null)
            return MapReport(report, report.InterviewSession.Questions, report.InterviewSession.Answers);

        var session = await dbContext.InterviewSessions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        if (session.Status == PracticeValues.Completing)
        {
            var latestJob = (await dbContext.OutboxEvents.AsNoTracking()
                    .Where(item => item.AggregateId == interviewId && item.Type == "InterviewReportRequested")
                    .ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .FirstOrDefault();
            if (latestJob?.Status == BillingValues.Failed)
                throw Conflict("INTERVIEW_REPORT_FAILED", "Báo cáo phỏng vấn chưa tạo được. Bạn có thể thử lại.");

            throw Conflict("INTERVIEW_REPORT_PROCESSING", "Báo cáo phỏng vấn đang được xử lý.");
        }

        throw Conflict("INTERVIEW_REPORT_UNAVAILABLE", "Báo cáo phỏng vấn chưa sẵn sàng.");
    }

    public async Task<DashboardView> GetDashboardAsync(Guid userId, CancellationToken cancellationToken)
    {
        var billing = await billingService.GetSummaryAsync(userId, cancellationToken);
        var isSqlite = string.Equals(
            dbContext.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.Sqlite",
            StringComparison.Ordinal);
        var interviewRowsQuery = dbContext.InterviewSessions.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.Role, item.Status, item.UpdatedAt });
        var interviewRows = isSqlite
            ? await dbContext.InterviewSessions
                .FromSqlInterpolated($"SELECT * FROM interview_sessions WHERE \"UserId\" = {userId} ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT 20")
                .AsNoTracking()
                .Select(item => new { item.Id, item.Role, item.Status, item.UpdatedAt })
                .ToArrayAsync(cancellationToken)
            : await interviewRowsQuery
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Id)
                .Take(20)
                .ToArrayAsync(cancellationToken);
        var interviews = interviewRows
            .Select(item => new InterviewSummary(item.Id, item.Role, item.Status, item.UpdatedAt))
            .ToArray();

        var reportRowsQuery = dbContext.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt });
        var reportRows = isSqlite
            ? await dbContext.InterviewReports
                .FromSqlInterpolated($"SELECT * FROM interview_reports WHERE \"UserId\" = {userId} ORDER BY \"CreatedAt\" DESC, \"Id\" DESC LIMIT 20")
                .AsNoTracking()
                .Select(item => new { item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt })
                .ToArrayAsync(cancellationToken)
            : await reportRowsQuery
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(20)
                .ToArrayAsync(cancellationToken);
        var reports = reportRows
            .Select(item => new ReportSummary(item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt))
            .ToArray();
        return new DashboardView(billing, interviews, reports);
    }

    private Task<bool> HasPendingReportJobAsync(Guid interviewId, CancellationToken cancellationToken) =>
        dbContext.OutboxEvents.AnyAsync(item => item.AggregateId == interviewId &&
            item.Type == "InterviewReportRequested" &&
            (item.Status == BillingValues.Pending || item.Status == BillingValues.Processing), cancellationToken);

    private Task<bool> HasPendingQuestionPlanJobAsync(Guid interviewId, CancellationToken cancellationToken) =>
        dbContext.OutboxEvents.AnyAsync(item => item.AggregateId == interviewId &&
            item.Type == "InterviewQuestionPlanRequested" &&
            (item.Status == BillingValues.Pending || item.Status == BillingValues.Processing), cancellationToken);

    private async Task<(Guid Id, string Status, DateTimeOffset CreatedAt)?> GetLatestQuestionPlanJobAsync(
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

    private async Task<string> GetQuestionPreparationStateAsync(
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

    private async Task TryQueueReportIfReadyAsync(
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
            await HasPendingReportJobAsync(interviewId, cancellationToken))
            return;

        dbContext.Add(Outbox("InterviewReportRequested", "interview", interviewId, now));
    }

    private static int PreparationLimit(int questionLimit) =>
        Math.Min(InterviewQuestionValues.MaxQuestionsPerSession,
            Math.Max(InterviewQuestionValues.FreeQuestionLimit, questionLimit));

    private static InterviewEvaluationProgress BuildEvaluationProgress(IEnumerable<InterviewAnswer> answers)
    {
        var states = answers.Select(item => item.EvaluationStatus).ToArray();
        return new(
            states.Length,
            states.Count(state => state == InterviewAnswerEvaluationStates.Queued),
            states.Count(state => state == InterviewAnswerEvaluationStates.Processing),
            states.Count(state => state == InterviewAnswerEvaluationStates.Ready),
            states.Count(state => state == InterviewAnswerEvaluationStates.Failed));
    }

    private static string GetResultState(
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

    private async Task<string> GetReportStateAsync(
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

    private async Task<InterviewContinuationView?> BuildContinuationAsync(
        Guid userId,
        InterviewSession session,
        CancellationToken cancellationToken)
    {
        var questionLimit = await GetQuestionLimitAsync(userId, cancellationToken);
        return BuildContinuation(session, questionLimit);
    }

    private static InterviewContinuationView? BuildContinuation(InterviewSession session, int questionLimit)
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

    private async Task<int> GetQuestionLimitAsync(Guid userId, CancellationToken cancellationToken)
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
