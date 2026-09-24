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
using static Nexora.Data.Practice.InterviewPersistence;
using static Nexora.Data.Practice.InterviewQuestionContracts;

namespace Nexora.Data.Practice;

public sealed class InterviewFlowService(NexoraDbContext dbContext, InterviewPersistence persistence, InterviewReadState readState, InterviewReportCoordinator reportCoordinator, IInterviewSessionService sessionService, TimeProvider timeProvider) : IInterviewFlowService
{
    public async Task<InterviewView> ContinueInterviewAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await persistence.FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null) return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        prior = await persistence.FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null) return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        var session = await persistence.FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        // A concurrent replay can wait on the interview row lock after its
        // initial idempotency reads. Re-check after acquiring the lock so it
        // replays the committed continuation instead of creating another
        // idempotency record or question-plan job.
        prior = await persistence.FindIdempotentAsync(userId, "interview.continue", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active) throw InvalidState();
        var questionLimit = await readState.GetQuestionLimitAsync(userId, cancellationToken);

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
            return await sessionService.GetInterviewAsync(userId, interviewId, cancellationToken);
        }

        if (session.Questions.Count >= questionLimit)
        {
            if (questionLimit <= InterviewQuestionValues.FreeQuestionLimit)
                throw InterviewUpgradeRequired();
            throw InterviewLimitReached();
        }
        if (await readState.HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
        {
            var pendingAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.continue", key, fingerprint, session.Id, pendingAt));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, interviewId, cancellationToken);
        }
        var now = timeProvider.GetUtcNow();
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(
            Idempotency(userId, "interview.continue", key, fingerprint, session.Id, now),
            Outbox("InterviewQuestionPlanRequested", "interview", session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await sessionService.GetInterviewAsync(userId, interviewId, cancellationToken);
    }

    public async Task<InterviewView> RetryQuestionPreparationAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await persistence.FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
            return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        prior = await persistence.FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }

        var session = await persistence.FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        // A concurrent replay can wait on the interview row lock after its
        // initial idempotency reads. Re-check after acquiring the lock so it
        // replays the committed retry instead of reporting a transient state
        // conflict.
        prior = await persistence.FindIdempotentAsync(userId, "interview.questions.retry", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active)
            throw InvalidState();

        var questionLimit = await readState.GetQuestionLimitAsync(userId, cancellationToken);
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
        if (await readState.HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
            throw new BusinessException(
                "INTERVIEW_QUESTION_PREPARATION_IN_PROGRESS",
                "Câu hỏi tiếp theo đang được chuẩn bị.",
                BusinessErrorKind.Conflict);

        var latestPlan = await readState.GetLatestQuestionPlanJobAsync(session.Id, cancellationToken);
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
        return await sessionService.GetInterviewAsync(userId, interviewId, cancellationToken);
    }

    public async Task<InterviewView> CompleteInterviewAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await persistence.FindIdempotentAsync(userId, "interview.complete", key, fingerprint, cancellationToken);
        if (prior is not null) return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);
        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var session = await persistence.FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await persistence.FindIdempotentAsync(userId, "interview.complete", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status == PracticeValues.Completed)
        {
            dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, timeProvider.GetUtcNow()));
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, session.Id, cancellationToken);
        }
        if (session.Status == PracticeValues.Completing)
        {
            var retryAt = timeProvider.GetUtcNow();
            dbContext.Add(Idempotency(userId, "interview.complete", key, fingerprint, session.Id, retryAt));
            await reportCoordinator.TryQueueReportIfReadyAsync(session.Id, retryAt, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, session.Id, cancellationToken);
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
        await reportCoordinator.TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await sessionService.GetInterviewAsync(userId, session.Id, cancellationToken);
    }

    public async Task<InterviewView> RetryReportAsync(Guid userId, Guid interviewId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await persistence.FindIdempotentAsync(userId, "interview.report.retry", key, fingerprint, cancellationToken);
        if (prior is not null) return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var session = await persistence.FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await persistence.FindIdempotentAsync(userId, "interview.report.retry", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status is not PracticeValues.Completing and not PracticeValues.Completed)
            throw InvalidState();

        var now = timeProvider.GetUtcNow();
        dbContext.Add(Idempotency(userId, "interview.report.retry", key, fingerprint, session.Id, now));
        if (session.Status == PracticeValues.Completing)
            await reportCoordinator.TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await sessionService.GetInterviewAsync(userId, session.Id, cancellationToken);
    }

    public async Task<InterviewView> RetryResultsAsync(
        Guid userId,
        Guid interviewId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId);
        var prior = await persistence.FindIdempotentAsync(userId, "interview.results.retry", key, fingerprint, cancellationToken);
        if (prior is not null) return await sessionService.GetInterviewAsync(userId, prior.ResourceId, cancellationToken);

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var session = await persistence.FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        var lockedPrior = await persistence.FindIdempotentAsync(userId, "interview.results.retry", key, fingerprint, cancellationToken);
        if (lockedPrior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await sessionService.GetInterviewAsync(userId, lockedPrior.ResourceId, cancellationToken);
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
            await reportCoordinator.TryQueueReportIfReadyAsync(session.Id, now, cancellationToken);
        }
        dbContext.Add(Idempotency(userId, "interview.results.retry", key, fingerprint, session.Id, now));
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return await sessionService.GetInterviewAsync(userId, session.Id, cancellationToken);
    }


}
