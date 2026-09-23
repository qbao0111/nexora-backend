using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class ScenarioStarService
{
    public async Task<StarAttemptView> CreateAsync(Guid userId, string question, string answer, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Trim().Length > 2_000) throw Validation("Câu hỏi không hợp lệ.");
        if (string.IsNullOrWhiteSpace(answer) || answer.Trim().Length > 12_000) throw Validation("Câu trả lời không hợp lệ.");
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(question.Trim(), answer.Trim());

        var prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "star-attempt.create" && item.Key == key, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
            return await GetStarAttemptAsync(userId, prior.ResourceId, cancellationToken);
        }

        await featureEntitlementService.RequireEnabledAsync(userId, FeatureValues.StarBuilder, cancellationToken);
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.StarBuilder, cancellationToken);

        var attemptId = Guid.NewGuid();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "star-attempt.create" && item.Key == key, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
            return await GetStarAttemptAsync(userId, prior.ResourceId, cancellationToken);
        }

        Guid? reservationId = null;
        if (access.Limit is not null)
        {
            try
            {
                var reservation = await featureEntitlementService.ReserveAsync(userId, FeatureValues.StarBuilder, attemptId.ToString("N"),
                    $"star:create:{key}", cancellationToken);
                reservationId = reservation.EventId;
            }
            catch (BusinessException ex) when (ex.Code == "IDEMPOTENCY_CONFLICT")
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                for (var i = 0; i < 10; i++)
                {
                    prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
                        item => item.ActorId == userId && item.Operation == "star-attempt.create" && item.Key == key, cancellationToken);
                    if (prior is not null) break;
                    await Task.Delay(25, cancellationToken);
                }
                if (prior is not null)
                {
                    if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                        throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
                    return await GetStarAttemptAsync(userId, prior.ResourceId, cancellationToken);
                }
                throw;
            }
        }

        var now = timeProvider.GetUtcNow();
        var attempt = new StarAttempt
        {
            Id = attemptId,
            UserId = userId,
            Question = question.Trim(),
            Answer = answer.Trim(),
            Status = PracticeFeatureValues.Queued,
            UsageReservationId = reservationId,
            ModelVersion = CurrentModelVersion,
            PromptVersion = PromptVersion,
            SchemaVersion = SchemaVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.StarAttempts.Add(attempt);
        dbContext.OutboxEvents.Add(new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Type = PracticeFeatureValues.StarEvaluationJob,
            AggregateType = "star_attempt",
            AggregateId = attemptId,
            Payload = JsonSerializer.Serialize(new { attemptId, userId }),
            Status = BillingValues.Pending,
            CreatedAt = now
        });
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Operation = "star-attempt.create",
            Key = key,
            RequestFingerprint = fingerprint,
            ResourceId = attemptId,
            CreatedAt = now
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapStarAttempt(attempt);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            for (var i = 0; i < 10; i++)
            {
                prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ActorId == userId && item.Operation == "star-attempt.create" && item.Key == key, cancellationToken);
                if (prior is not null) break;
                await Task.Delay(25, cancellationToken);
            }
            if (prior is not null)
            {
                if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
                return await GetStarAttemptAsync(userId, prior.ResourceId, cancellationToken);
            }
            throw;
        }
    }

    public async Task<StarAttemptView> GetAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken) =>
        await GetStarAttemptAsync(userId, attemptId, cancellationToken);

    public async Task<IReadOnlyCollection<StarAttemptView>> GetManyAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.StarAttempts.AsNoTracking().Where(item => item.UserId == userId).OrderByDescending(item => item.CreatedAt).Take(20)
            .Select(item => new StarAttemptView(item.Id, item.Question, item.Answer, item.Status,
                Parse(item.EvaluationJson), item.ErrorCode, item.CreatedAt, item.CompletedAt))
            .ToArrayAsync(cancellationToken);

    private async Task<StarAttemptView> GetStarAttemptAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken)
    {
        var attempt = await dbContext.StarAttempts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == attemptId && item.UserId == userId, cancellationToken)
            ?? throw new BusinessException("STAR_ATTEMPT_NOT_FOUND", "Không tìm thấy STAR attempt.", BusinessErrorKind.NotFound);
        return MapStarAttempt(attempt);
    }



}
