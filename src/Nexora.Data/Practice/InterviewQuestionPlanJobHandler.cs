using System.Text.Json;
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

public sealed class InterviewQuestionPlanJobHandler(NexoraDbContext dbContext, InterviewPersistence persistence, InterviewReadState readState, InterviewQuestionGenerator questionGenerator, TimeProvider timeProvider)
{
    internal Task ProcessAsync(OutboxEvent job, CancellationToken cancellationToken) => PrepareInterviewQuestionsAsync(job, cancellationToken);
    private void MarkProcessed(OutboxEvent job) { job.Status = BillingValues.Processed; job.ProcessedAt = timeProvider.GetUtcNow(); }

    private async Task PrepareInterviewQuestionsAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.AsNoTracking()
            .Include(item => item.Resume)
            .Include(item => item.JobDescription)
            .SingleOrDefaultAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot is null || snapshot.Status != PracticeValues.Active)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var questionLimit = await readState.GetQuestionLimitAsync(snapshot.UserId, cancellationToken);
        var existingMax = await dbContext.InterviewQuestions.AsNoTracking()
            .Where(item => item.InterviewSessionId == snapshot.Id)
            .Select(item => (int?)item.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        var endSequence = InterviewReadState.PreparationLimit(questionLimit);
        if (existingMax >= endSequence)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var hasUsableResumeContext = HasUsableResumeContext(snapshot.Resume);
        var profile = hasUsableResumeContext
            ? ResumeProfileProcessor.TryReadResumeProfile(snapshot.Resume?.StructuredProfile)
            : null;
        var existingQuestions = await dbContext.InterviewQuestions.AsNoTracking()
            .Where(item => item.InterviewSessionId == snapshot.Id)
            .OrderBy(item => item.Sequence)
            .Select(item => new { item.Topic, item.Content })
            .ToArrayAsync(cancellationToken);
        var prepared = await questionGenerator.GeneratePreparedQuestionsAsync(
            snapshot,
            profile,
            existingMax + 1,
            endSequence,
            hasUsableResumeContext,
            cancellationToken,
            existingQuestions.Select(item => (item.Topic, item.Content)).ToArray());

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var session = await persistence.FindInterviewForUpdateAsync(snapshot.UserId, snapshot.Id, cancellationToken) ?? throw NotFound();
        await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
        await dbContext.Entry(session).Collection(item => item.Answers).LoadAsync(cancellationToken);
        if (session.Status == PracticeValues.Active)
        {
            var known = session.Questions.Select(item => item.Sequence).ToHashSet();
            var now = timeProvider.GetUtcNow();
            foreach (var item in prepared.Where(item => known.Add(item.Sequence)))
            {
                dbContext.InterviewQuestions.Add(new InterviewQuestion
                {
                    Id = Guid.NewGuid(),
                    InterviewSessionId = session.Id,
                    Sequence = item.Sequence,
                    Kind = InterviewQuestionValues.Primary,
                    Topic = item.Topic,
                    Content = item.Content,
                    PromptVersion = item.PromptVersion,
                    ModelVersion = item.ModelVersion,
                    CreatedAt = now
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await dbContext.Entry(session).Collection(item => item.Questions).LoadAsync(cancellationToken);
            var next = session.Questions
                .Where(item => item.ReleasedAt is null && item.Sequence <= questionLimit)
                .OrderBy(item => item.Sequence)
                .FirstOrDefault(item => session.Questions
                    .Where(previous => previous.Sequence < item.Sequence && previous.ReleasedAt is not null)
                    .All(previous => session.Answers.Any(answer => answer.QuestionId == previous.Id)));
            if (next is not null)
            {
                next.ReleasedAt = now;
                session.Version++;
                session.UpdatedAt = now;
                persistence.EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
            }
        }

        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    internal async Task FailAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var current = await dbContext.OutboxEvents.SingleAsync(item => item.Id == job.Id, cancellationToken);
        current.Status = PracticeValues.Failed;
        current.ProcessedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }
}
