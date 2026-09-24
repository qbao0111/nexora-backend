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

public sealed class InterviewStartJobHandler(NexoraDbContext dbContext, InterviewPersistence persistence, InterviewReadState readState, InterviewQuestionGenerator questionGenerator, ResumeProfileProcessor resumeProfileProcessor, TimeProvider timeProvider)
{
    internal Task ProcessAsync(OutboxEvent job, CancellationToken cancellationToken) => ActivateInterviewAsync(job, cancellationToken);
    private void MarkProcessed(OutboxEvent job) { job.Status = BillingValues.Processed; job.ProcessedAt = timeProvider.GetUtcNow(); }

    private async Task ActivateInterviewAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.Include(item => item.Resume).Include(item => item.JobDescription)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); return; }

        await using (var activationGate = await persistence.BeginTransactionAsync(cancellationToken))
        {
            await persistence.LockUserAsync(snapshot.UserId, cancellationToken);
            await dbContext.Entry(snapshot).ReloadAsync(cancellationToken);
            if (snapshot.Resume is not null)
                await dbContext.Entry(snapshot.Resume).ReloadAsync(cancellationToken);
            if (snapshot.Status == PracticeValues.Starting && snapshot.Resume?.DeletedAt is not null)
            {
                var deletedReservation = await dbContext.UsageEvents.AsNoTracking()
                    .SingleAsync(item => item.Id == snapshot.ReservationEventId, cancellationToken);
                var deletedEntitlement = await persistence.FindEntitlementForUpdateAsync(deletedReservation.EntitlementId, cancellationToken)
                    ?? throw InvalidState();
                var deletionGateAt = timeProvider.GetUtcNow();
                persistence.FinalizeReservation(deletedEntitlement, deletedReservation, BillingValues.Void, deletionGateAt);
                snapshot.Status = PracticeValues.Failed;
                snapshot.Version++;
                snapshot.UpdatedAt = deletionGateAt;
                persistence.EnqueueResourceChanged(snapshot.UserId, "interview", snapshot.Id, snapshot.Status, deletionGateAt);
                MarkProcessed(job);
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(activationGate, cancellationToken);
                return;
            }
            await CommitAsync(activationGate, cancellationToken);
        }

        if (snapshot.Status != PracticeValues.Starting)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }
        var hasUsableResumeContext = HasUsableResumeContext(snapshot.Resume);
        var profile = hasUsableResumeContext
            ? await resumeProfileProcessor.EnsureResumeProfileAsync(snapshot.Resume!, snapshot.Id, cancellationToken)
            : null;
        var questionLimit = await readState.GetQuestionLimitAsync(snapshot.UserId, cancellationToken);
        var preparedQuestions = await questionGenerator.GeneratePreparedQuestionsAsync(
            snapshot,
            profile,
            startSequence: 1,
            endSequence: InterviewReadState.PreparationLimit(questionLimit),
            hasUsableResumeContext,
            cancellationToken);

        await using var transaction = await persistence.BeginTransactionAsync(cancellationToken);
        var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (session.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); await CommitAsync(transaction, cancellationToken); return; }
        var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
        var entitlement = await persistence.FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
        var now = timeProvider.GetUtcNow();
        dbContext.InterviewQuestions.AddRange(preparedQuestions.Select((prepared, index) => new InterviewQuestion
        {
            Id = Guid.NewGuid(),
            InterviewSessionId = session.Id,
            Sequence = prepared.Sequence,
            Kind = InterviewQuestionValues.Primary,
            Topic = prepared.Topic,
            Content = prepared.Content,
            PromptVersion = prepared.PromptVersion,
            ModelVersion = prepared.ModelVersion,
            CreatedAt = now,
            ReleasedAt = index == 0 ? now : null
        }));
        persistence.FinalizeReservation(entitlement, reservation, BillingValues.Consume, now);
        session.Status = PracticeValues.Active;
        persistence.EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
        session.Version++;
        session.UpdatedAt = now;
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
        {
            var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            if (session.Status == PracticeValues.Starting)
            {
                var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
                var entitlement = await persistence.FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
                persistence.FinalizeReservation(entitlement, reservation, BillingValues.Void, current.ProcessedAt.Value);
                session.Status = PracticeValues.Failed;
                persistence.EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, current.ProcessedAt.Value);
                session.Version++;
                session.UpdatedAt = current.ProcessedAt.Value;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }
}
