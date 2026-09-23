using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class ScenarioStarService
{
    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.AddMinutes(-10);
        var relevant = dbContext.OutboxEvents.AsNoTracking().Where(item =>
            item.Type == PracticeFeatureValues.ScenarioEvaluationJob || item.Type == PracticeFeatureValues.StarEvaluationJob);
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
            try
            {
                switch (job.Type)
                {
                    case "ScenarioEvaluationRequested": await EvaluateScenarioAsync(job, cancellationToken); break;
                    case "StarEvaluationRequested": await EvaluateStarAsync(job, cancellationToken); break;
                }
                JobCompleted(logger, job.Id, job.Type, job.AggregateId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await FailJobAsync(job, exception, cancellationToken);
                JobFailed(logger, exception, job.Id, job.Type, job.AggregateId);
            }
        }
        return claimedCount;
    }

    private async Task EvaluateScenarioAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var attempt = await dbContext.ScenarioAttempts.Include(item => item.Scenario)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        attempt.Status = PracticeFeatureValues.Processing;
        attempt.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        var input = $"Scenario: {attempt.Scenario.Title}\nCompetency: {attempt.Scenario.Competency}\nDifficulty: {attempt.Scenario.Difficulty}\n\nContent:\n{attempt.Scenario.Content}\n\nUser Answer:\n{attempt.Answer}";
        var execResult = await structuredAiExecutor.ExecuteAsync(
            AiOperations.ScenarioEvaluate,
            Bound(input),
            new AiOperationContext(attempt.Id.ToString("N"), attempt.UserId),
            cancellationToken);
        var result = execResult.Value;

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        attempt.EvaluationJson = JsonSerializer.Serialize(result, JsonOptions);
        attempt.Status = PracticeFeatureValues.Completed;
        attempt.CompletedAt = attempt.UpdatedAt = timeProvider.GetUtcNow();
        EnqueueResourceChanged(attempt.UserId, "scenarioAttempt", attempt.Id, attempt.Status, attempt.CompletedAt.Value);
        MarkProcessed(job, timeProvider.GetUtcNow());
        if (attempt.UsageReservationId.HasValue)
            await featureEntitlementService.ConsumeAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task EvaluateStarAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var attempt = await dbContext.StarAttempts.SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        attempt.Status = PracticeFeatureValues.Processing;
        attempt.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        var input = $"Question: {attempt.Question}\n\nAnswer: {attempt.Answer}";
        var execResult = await structuredAiExecutor.ExecuteAsync(
            AiOperations.StarEvaluate,
            Bound(input),
            new AiOperationContext(attempt.Id.ToString("N"), attempt.UserId, ExpectedStar: true),
            cancellationToken);
        var evaluation = execResult.Value;

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        attempt.EvaluationJson = JsonSerializer.Serialize(evaluation, JsonOptions);
        attempt.Status = PracticeFeatureValues.Completed;
        attempt.CompletedAt = attempt.UpdatedAt = timeProvider.GetUtcNow();
        EnqueueResourceChanged(attempt.UserId, "starAttempt", attempt.Id, attempt.Status, attempt.CompletedAt.Value);
        MarkProcessed(job, timeProvider.GetUtcNow());
        if (attempt.UsageReservationId.HasValue)
            await featureEntitlementService.ConsumeAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task FailJobAsync(OutboxEvent job, Exception exception, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var current = await dbContext.OutboxEvents.SingleAsync(item => item.Id == job.Id, cancellationToken);
        current.Status = PracticeFeatureValues.Failed;
        current.ProcessedAt = timeProvider.GetUtcNow();

        if (current.Type == PracticeFeatureValues.ScenarioEvaluationJob)
        {
            var attempt = await dbContext.ScenarioAttempts.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            attempt.Status = PracticeFeatureValues.Failed;
            attempt.ErrorCode = "AI_PROCESSING_FAILED";
            attempt.UpdatedAt = current.ProcessedAt.Value;
            if (attempt.UsageReservationId.HasValue)
                await featureEntitlementService.VoidAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
            EnqueueResourceChanged(attempt.UserId, "scenarioAttempt", attempt.Id, attempt.Status, attempt.UpdatedAt);
        }
        else if (current.Type == PracticeFeatureValues.StarEvaluationJob)
        {
            var attempt = await dbContext.StarAttempts.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            attempt.Status = PracticeFeatureValues.Failed;
            attempt.ErrorCode = "AI_PROCESSING_FAILED";
            attempt.UpdatedAt = current.ProcessedAt.Value;
            if (attempt.UsageReservationId.HasValue)
                await featureEntitlementService.VoidAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
            EnqueueResourceChanged(attempt.UserId, "starAttempt", attempt.Id, attempt.Status, attempt.UpdatedAt);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    [LoggerMessage(LogLevel.Information, "Job {JobId} ({JobType}/{AggregateId}) completed")]
    private static partial void JobCompleted(ILogger logger, Guid jobId, string jobType, Guid aggregateId);
    [LoggerMessage(LogLevel.Error, "Job {JobId} ({JobType}/{AggregateId}) failed")]
    private static partial void JobFailed(ILogger logger, Exception exception, Guid jobId, string jobType, Guid aggregateId);
}
