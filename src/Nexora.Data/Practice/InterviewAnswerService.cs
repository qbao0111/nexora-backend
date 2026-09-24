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
using static Nexora.Data.Practice.InterviewViewAssembler;

namespace Nexora.Data.Practice;

public sealed class InterviewAnswerService(
    NexoraDbContext dbContext,
    InterviewPersistence persistence,
    InterviewReadState readState,
    TimeProvider timeProvider) : IInterviewAnswerService
{
    public async Task<AnswerResult> SubmitAnswerAsync(
        Guid userId, Guid interviewId, Guid questionId, string content, int? durationSeconds, string idempotencyKey, CancellationToken cancellationToken)
    {
        var normalizedContent = content?.Trim() ?? string.Empty;
        if (normalizedContent.Length is 0 or > 12_000 || durationSeconds is < 0 or > 7200)
            throw Validation("Câu trả lời không hợp lệ.");
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(interviewId, questionId, normalizedContent, durationSeconds);
        var prior = await persistence.FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
        if (prior is not null) return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        prior = await persistence.FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
        if (prior is not null) return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
        var session = await persistence.FindInterviewForUpdateAsync(userId, interviewId, cancellationToken) ?? throw NotFound();
        // A concurrent replay can wait on the session lock after its initial
        // idempotency read. Re-check after the lock so it replays the committed
        // answer instead of colliding with the official-answer unique key.
        prior = await persistence.FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
        if (prior is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
        }
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        ValidateQuestionContracts(session.Questions);
        if (session.Status != PracticeValues.Active) throw InvalidState();
        var question = session.Questions.SingleOrDefault(item => item.Id == questionId) ?? throw NotFound();
        var questionLimit = await readState.GetQuestionLimitAsync(userId, cancellationToken);
        if (question.ReleasedAt is null || question.Sequence > questionLimit)
            throw InvalidState();
        if (session.Answers.Any(item => item.QuestionId == questionId))
            throw Conflict("ANSWER_ALREADY_EXISTS", "Câu hỏi đã có câu trả lời chính thức.");
        if (session.Questions.Any(item => item.Sequence < question.Sequence &&
                item.ReleasedAt is not null && session.Answers.All(answer => answer.QuestionId != item.Id)))
            throw InvalidState();

        var now = timeProvider.GetUtcNow();
        var answer = new InterviewAnswer
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InterviewSessionId = session.Id,
            QuestionId = question.Id,
            Content = normalizedContent,
            DurationSeconds = durationSeconds,
            Evaluation = null,
            EvaluationStatus = InterviewAnswerEvaluationStates.Queued,
            CreatedAt = now
        };
        dbContext.Add(answer);
        var nextQuestion = session.Questions
            .Where(item => item.Sequence > question.Sequence && item.Sequence <= questionLimit && item.ReleasedAt is null)
            .OrderBy(item => item.Sequence)
            .FirstOrDefault();
        if (nextQuestion is not null)
        {
            nextQuestion.ReleasedAt = now;
        }
        else if (session.Questions.Max(item => item.Sequence) < questionLimit &&
                 !await readState.HasPendingQuestionPlanJobAsync(session.Id, cancellationToken))
        {
            dbContext.Add(Outbox("InterviewQuestionPlanRequested", "interview", session.Id, now));
        }
        session.Version++;
        session.UpdatedAt = now;
        dbContext.AddRange(
            Outbox("InterviewAnswerEvaluationRequested", "interviewAnswer", answer.Id, now),
            Idempotency(userId, "interview.answer", key, fingerprint, answer.Id, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            prior = await persistence.FindIdempotentAsync(userId, "interview.answer", key, fingerprint, cancellationToken);
            if (prior is not null) return await MapExistingAnswerAsync(userId, interviewId, prior.ResourceId, cancellationToken);
            throw Conflict("CONCURRENT_SUBMISSION", "Câu trả lời đang được xử lý hoặc đã được nộp.");
        }
        var continuation = await readState.BuildContinuationAsync(userId, session, cancellationToken);
        var isComplete = nextQuestion is null && continuation?.State == InterviewContinuationValues.MaxQuestionsReached;
        return new AnswerResult(MapAnswer(answer, session.Status), nextQuestion is null ? null : MapQuestion(nextQuestion), isComplete, continuation);
    }

    private async Task<AnswerResult> MapExistingAnswerAsync(Guid userId, Guid interviewId, Guid answerId, CancellationToken cancellationToken)
    {
        var session = await dbContext.InterviewSessions.AsNoTracking().Include(item => item.Questions).Include(item => item.Answers)
            .SingleOrDefaultAsync(item => item.Id == interviewId && item.UserId == userId, cancellationToken) ?? throw NotFound();
        ValidateQuestionContracts(session.Questions);
        var answer = session.Answers.Single(item => item.Id == answerId);
        var questionLimit = await readState.GetQuestionLimitAsync(userId, cancellationToken);
        var next = session.Questions
            .OrderBy(item => item.Sequence)
            .FirstOrDefault(item => item.Sequence <= questionLimit &&
                item.ReleasedAt is not null &&
                session.Answers.All(existing => existing.QuestionId != item.Id));
        var continuation = await readState.BuildContinuationAsync(userId, session, cancellationToken);
        var isComplete = next is null && continuation?.State == InterviewContinuationValues.MaxQuestionsReached;
        return new AnswerResult(MapAnswer(answer, session.Status), next is null ? null : MapQuestion(next), isComplete, continuation);
    }

}
