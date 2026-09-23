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

namespace Nexora.Data.Practice;

public sealed partial class PracticeService
{
    private async Task ActivateInterviewAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var snapshot = await dbContext.InterviewSessions.Include(item => item.Resume).Include(item => item.JobDescription)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (snapshot.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); return; }

        await using (var activationGate = await BeginTransactionAsync(cancellationToken))
        {
            await LockUserAsync(snapshot.UserId, cancellationToken);
            await dbContext.Entry(snapshot).ReloadAsync(cancellationToken);
            if (snapshot.Resume is not null)
                await dbContext.Entry(snapshot.Resume).ReloadAsync(cancellationToken);
            if (snapshot.Status == PracticeValues.Starting && snapshot.Resume?.DeletedAt is not null)
            {
                var deletedReservation = await dbContext.UsageEvents.AsNoTracking()
                    .SingleAsync(item => item.Id == snapshot.ReservationEventId, cancellationToken);
                var deletedEntitlement = await FindEntitlementForUpdateAsync(deletedReservation.EntitlementId, cancellationToken)
                    ?? throw InvalidState();
                var deletionGateAt = timeProvider.GetUtcNow();
                FinalizeReservation(deletedEntitlement, deletedReservation, BillingValues.Void, deletionGateAt);
                snapshot.Status = PracticeValues.Failed;
                snapshot.Version++;
                snapshot.UpdatedAt = deletionGateAt;
                EnqueueResourceChanged(snapshot.UserId, "interview", snapshot.Id, snapshot.Status, deletionGateAt);
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
            ? await EnsureResumeProfileAsync(snapshot.Resume!, snapshot.Id, cancellationToken)
            : null;
        var questionLimit = await GetQuestionLimitAsync(snapshot.UserId, cancellationToken);
        var preparedQuestions = await GeneratePreparedQuestionsAsync(
            snapshot,
            profile,
            startSequence: 1,
            endSequence: PreparationLimit(questionLimit),
            hasUsableResumeContext,
            cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await dbContext.InterviewSessions.SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        if (session.Status != PracticeValues.Starting) { MarkProcessed(job); await dbContext.SaveChangesAsync(cancellationToken); await CommitAsync(transaction, cancellationToken); return; }
        var reservation = await dbContext.UsageEvents.AsNoTracking().SingleAsync(item => item.Id == session.ReservationEventId, cancellationToken);
        var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken) ?? throw InvalidState();
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
        FinalizeReservation(entitlement, reservation, BillingValues.Consume, now);
        session.Status = PracticeValues.Active;
        EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
        session.Version++;
        session.UpdatedAt = now;
        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task<IReadOnlyCollection<PreparedInterviewQuestion>> GeneratePreparedQuestionsAsync(
        InterviewSession session,
        ResumeProfile? profile,
        int startSequence,
        int endSequence,
        bool hasUsableResumeContext,
        CancellationToken cancellationToken,
        IReadOnlyCollection<(string Topic, string Content)>? existingQuestions = null)
    {
        var prepared = new List<PreparedInterviewQuestion>();
        var previousQuestions = existingQuestions?.ToList() ?? [];
        for (var sequence = startSequence; sequence <= endSequence; sequence++)
        {
            var topic = sequence == 1 && !string.IsNullOrWhiteSpace(session.FocusTopic)
                ? session.FocusTopic!
                : InterviewQuestionValues.TopicForSequence(
                        session.InterviewType,
                        sequence,
                        hasUsableResumeContext,
                        session.JobDescription is not null);
            var context = resumeContextBuilder.BuildInterviewQuestionContext(
                session.Role,
                session.Seniority,
                session.InterviewType,
                session.Difficulty,
                session.JobDescription?.Content,
                profile,
                sequence,
                topic,
                previousQuestions);
            var result = await structuredAiExecutor.ExecuteAsync<GeneratedQuestion>(
                AiOperations.InterviewFirstQuestion,
                context,
                new AiOperationContext(
                    session.Id.ToString("N"),
                    session.UserId,
                    Metadata: new Dictionary<string, string>
                    {
                        ["questionSequence"] = sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["questionKind"] = InterviewQuestionValues.Primary,
                        ["questionTopic"] = topic
                    },
                    PreviousQuestions: previousQuestions.Select(item => item.Content).ToArray()),
                cancellationToken);
            var content = result.Value.Content?.Trim() ?? string.Empty;
            if (content.Length == 0) throw InvalidAiOutput();
            prepared.Add(new PreparedInterviewQuestion(
                sequence,
                topic,
                content[..Math.Min(content.Length, 2_000)],
                result.PromptVersion,
                result.ModelVersion));
            previousQuestions.Add((topic, content));
        }

        return prepared;
    }

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

        var questionLimit = await GetQuestionLimitAsync(snapshot.UserId, cancellationToken);
        var existingMax = await dbContext.InterviewQuestions.AsNoTracking()
            .Where(item => item.InterviewSessionId == snapshot.Id)
            .Select(item => (int?)item.Sequence)
            .MaxAsync(cancellationToken) ?? 0;
        var endSequence = PreparationLimit(questionLimit);
        if (existingMax >= endSequence)
        {
            MarkProcessed(job);
            await dbContext.SaveChangesAsync(cancellationToken);
            return;
        }

        var hasUsableResumeContext = HasUsableResumeContext(snapshot.Resume);
        var profile = hasUsableResumeContext
            ? TryReadResumeProfile(snapshot.Resume?.StructuredProfile)
            : null;
        var existingQuestions = await dbContext.InterviewQuestions.AsNoTracking()
            .Where(item => item.InterviewSessionId == snapshot.Id)
            .OrderBy(item => item.Sequence)
            .Select(item => new { item.Topic, item.Content })
            .ToArrayAsync(cancellationToken);
        var prepared = await GeneratePreparedQuestionsAsync(
            snapshot,
            profile,
            existingMax + 1,
            endSequence,
            hasUsableResumeContext,
            cancellationToken,
            existingQuestions.Select(item => (item.Topic, item.Content)).ToArray());

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var session = await FindInterviewForUpdateAsync(snapshot.UserId, snapshot.Id, cancellationToken) ?? throw NotFound();
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
                EnqueueResourceChanged(session.UserId, "interview", session.Id, session.Status, now);
            }
        }

        MarkProcessed(job);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private static string[]? ReadMissingStarElements(string? evaluation)
    {
        if (string.IsNullOrWhiteSpace(evaluation)) return null;
        try
        {
            using var document = JsonDocument.Parse(evaluation);
            if (!document.RootElement.TryGetProperty("star", out var star) ||
                !star.TryGetProperty("missingElements", out var missing) ||
                missing.ValueKind != JsonValueKind.Array)
                return null;
            return missing.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ValidateQuestionContracts(IEnumerable<InterviewQuestion> questions)
    {
        var questionMap = questions.ToDictionary(item => item.Id);
        foreach (var question in questionMap.Values)
        {
            if (!InterviewQuestionValues.IsSupportedKind(question.Kind) ||
                string.IsNullOrWhiteSpace(question.Topic) || question.Topic.Trim().Length > 80)
                throw InvalidState();

            if (string.Equals(question.Kind, InterviewQuestionValues.Primary, StringComparison.Ordinal))
            {
                if (question.ParentQuestionId is not null) throw InvalidState();
                continue;
            }

            if (question.ParentQuestionId is null ||
                !questionMap.TryGetValue(question.ParentQuestionId.Value, out var parent) ||
                parent.Id == question.Id ||
                parent.InterviewSessionId != question.InterviewSessionId ||
                parent.Sequence >= question.Sequence ||
                !string.Equals(parent.Topic, question.Topic, StringComparison.Ordinal))
                throw InvalidState();
        }
    }

    private sealed record PersistedStarEvaluation(
        Guid AnswerId,
        int Sequence,
        Guid RootQuestionId,
        DateTimeOffset CreatedAt,
        StarEvaluation Star);

    private sealed record StarStorySummary(
        Guid RootQuestionId,
        int RootSequence,
        IReadOnlyCollection<PersistedStarEvaluation> Evaluations);

    private sealed record InterviewStartContext(
        string Role,
        string Seniority,
        string InterviewType,
        string Difficulty,
        Guid? ResumeId,
        Guid? JobDescriptionId,
        Guid? CareerGoalId,
        Guid? SourceInterviewId,
        Guid? SourceQuestionId,
        string? PracticeReason,
        string? FocusTopic);

    private sealed record PreparedInterviewQuestion(
        int Sequence,
        string Topic,
        string Content,
        string PromptVersion,
        string ModelVersion);

}
