using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Billing;

public sealed partial class FeatureEntitlementService(
    NexoraDbContext dbContext,
    IBillingService billingService,
    TimeProvider timeProvider) : IFeatureEntitlementService
{
    public async Task<FeatureAccessView> GetAsync(Guid userId, string featureCode, CancellationToken cancellationToken)
    {
        var entitlement = await FindActiveEntitlementAsync(userId, cancellationToken);
        if (entitlement is null) return Disabled(featureCode);
        if (featureCode == FeatureValues.Interview)
        {
            var limit = entitlement.InterviewLimit;
            return new FeatureAccessView(FeatureValues.Interview, true, limit, entitlement.Reserved, entitlement.Consumed,
                entitlement.Adjustment, Available(limit, entitlement.Reserved, entitlement.Consumed, entitlement.Adjustment), limit is null);
        }
        var ef = await dbContext.EntitlementFeatures.AsNoTracking()
            .SingleOrDefaultAsync(item => item.EntitlementId == entitlement.Id && item.FeatureCode == featureCode, cancellationToken);
        if (ef is null) return Disabled(featureCode);
        return new FeatureAccessView(ef.FeatureCode, ef.IsEnabled, ef.Limit, ef.Reserved, ef.Consumed, ef.Adjustment,
            Available(ef.Limit, ef.Reserved, ef.Consumed, ef.Adjustment), ef.IsEnabled && ef.Limit is null);
    }

    public async Task RequireEnabledAsync(Guid userId, string featureCode, CancellationToken cancellationToken)
    {
        var access = await GetAsync(userId, featureCode, cancellationToken);
        if (!access.Enabled) throw FeatureUnavailable();
    }

    public async Task<FeatureReservation> ReserveAsync(
        Guid userId, string featureCode, string sourceId, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (featureCode == FeatureValues.Interview)
        {
            var reservation = await billingService.ReserveInterviewAsync(userId, sourceId, idempotencyKey, cancellationToken);
            return new FeatureReservation(reservation.EventId, reservation.EntitlementId, reservation.Available);
        }
        return await ReserveGenericAsync(userId, featureCode, sourceId, idempotencyKey, cancellationToken);
    }

    public async Task ConsumeAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken)
    {
        var reservation = await dbContext.FeatureUsageEvents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == reservationEventId && item.UserId == userId && item.Action == FeatureValues.Reserve, cancellationToken);
        if (reservation is not null)
        {
            await CompleteGenericAsync(userId, reservation, FeatureValues.Consume, cancellationToken);
            return;
        }
        await billingService.ConsumeReservationAsync(userId, reservationEventId, cancellationToken);
    }

    public async Task VoidAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken)
    {
        var reservation = await dbContext.FeatureUsageEvents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == reservationEventId && item.UserId == userId && item.Action == FeatureValues.Reserve, cancellationToken);
        if (reservation is not null)
        {
            await CompleteGenericAsync(userId, reservation, FeatureValues.Void, cancellationToken);
            return;
        }
        await billingService.VoidReservationAsync(userId, reservationEventId, cancellationToken);
    }

    public async Task AdjustAsync(
        Guid userId, string featureCode, int quantity, string reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (quantity == 0 || string.IsNullOrWhiteSpace(reason))
            throw new BusinessException("INVALID_ADJUSTMENT", "Adjustment cần số lượng khác 0 và lý do.", BusinessErrorKind.Validation);
        if (featureCode == FeatureValues.Interview)
        {
            await billingService.AdjustInterviewQuotaAsync(userId, quantity, reason, idempotencyKey, cancellationToken);
            return;
        }
        await AdjustGenericAsync(userId, featureCode, quantity, reason, idempotencyKey, cancellationToken);
    }

    private async Task<FeatureReservation> ReserveGenericAsync(
        Guid userId, string featureCode, string sourceId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var existing = await FindGenericByKeyAsync(userId, FeatureValues.Reserve, key, cancellationToken);
        if (existing is not null) return await MapGenericReservationAsync(existing, sourceId, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        existing = await FindGenericByKeyAsync(userId, FeatureValues.Reserve, key, cancellationToken);
        if (existing is not null) return await MapGenericReservationAsync(existing, sourceId, cancellationToken);
        var entitlement = await FindActiveEntitlementForUpdateAsync(userId, cancellationToken) ?? throw FeatureUnavailable();
        var ef = await FindEntitlementFeatureForUpdateAsync(entitlement.Id, featureCode, cancellationToken);
        if (ef is null || !ef.IsEnabled) throw FeatureUnavailable();
        existing = await FindGenericByKeyAsync(userId, FeatureValues.Reserve, key, cancellationToken);
        if (existing is not null) return await MapGenericReservationAsync(existing, sourceId, cancellationToken);
        if (ef.Limit is not null && Available(ef.Limit, ef.Reserved, ef.Consumed, ef.Adjustment) < 1)
            throw FeatureQuotaExceeded();

        var now = timeProvider.GetUtcNow();
        var usage = new FeatureUsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementFeatureId = ef.Id,
            FeatureCode = featureCode,
            Action = FeatureValues.Reserve,
            Quantity = 1,
            SourceType = featureCode,
            SourceId = RequireSource(sourceId),
            IdempotencyKey = key,
            CreatedAt = now
        };
        ef.Reserved++;
        ef.UpdatedAt = now;
        ef.ConcurrencyToken = Guid.NewGuid();
        dbContext.FeatureUsageEvents.Add(usage);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        return new FeatureReservation(usage.Id, ef.Id, Available(ef.Limit, ef.Reserved, ef.Consumed, ef.Adjustment));
    }

    private async Task CompleteGenericAsync(Guid userId, FeatureUsageEvent reservation, string action, CancellationToken cancellationToken)
    {
        var sourceId = reservation.Id.ToString("N");
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var existing = await dbContext.FeatureUsageEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.EntitlementFeatureId == reservation.EntitlementFeatureId && item.SourceId == sourceId &&
                (item.Action == FeatureValues.Consume || item.Action == FeatureValues.Void), cancellationToken);
        if (existing is not null)
        {
            if (existing.Action == action) return;
            throw new BusinessException("RESERVATION_ALREADY_FINALIZED", "Reservation đã được xử lý.", BusinessErrorKind.Conflict);
        }
        var ef = await FindEntitlementFeatureForUpdateAsync(reservation.EntitlementFeatureId, cancellationToken)
            ?? throw new BusinessException("ENTITLEMENT_FEATURE_NOT_FOUND", "Không tìm thấy feature entitlement.", BusinessErrorKind.NotFound);
        existing = await dbContext.FeatureUsageEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.EntitlementFeatureId == reservation.EntitlementFeatureId && item.SourceId == sourceId &&
                (item.Action == FeatureValues.Consume || item.Action == FeatureValues.Void), cancellationToken);
        if (existing is not null)
        {
            if (existing.Action == action) return;
            throw new BusinessException("RESERVATION_ALREADY_FINALIZED", "Reservation đã được xử lý.", BusinessErrorKind.Conflict);
        }
        if (ef.Reserved < reservation.Quantity)
            throw new BusinessException("INVALID_USAGE_STATE", "Feature usage ledger không ở trạng thái hợp lệ.", BusinessErrorKind.Conflict);
        var now = timeProvider.GetUtcNow();
        ef.Reserved -= reservation.Quantity;
        if (action == FeatureValues.Consume) ef.Consumed += reservation.Quantity;
        ef.UpdatedAt = now;
        ef.ConcurrencyToken = Guid.NewGuid();
        dbContext.FeatureUsageEvents.Add(new FeatureUsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementFeatureId = ef.Id,
            FeatureCode = ef.FeatureCode,
            Action = action,
            Quantity = reservation.Quantity,
            SourceType = "reservation",
            SourceId = sourceId,
            IdempotencyKey = $"{action}:{sourceId}",
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task AdjustGenericAsync(
        Guid userId, string featureCode, int quantity, string reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var existing = await FindGenericByKeyAsync(userId, FeatureValues.Adjustment, key, cancellationToken);
        if (existing is not null)
        {
            if (existing.Quantity != quantity || !string.Equals(existing.Reason, reason.Trim(), StringComparison.Ordinal))
                throw IdempotencyConflict();
            return;
        }
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        existing = await FindGenericByKeyAsync(userId, FeatureValues.Adjustment, key, cancellationToken);
        if (existing is not null)
        {
            if (existing.Quantity != quantity || !string.Equals(existing.Reason, reason.Trim(), StringComparison.Ordinal))
                throw IdempotencyConflict();
            return;
        }
        var entitlement = await FindActiveEntitlementForUpdateAsync(userId, cancellationToken) ?? throw FeatureUnavailable();
        var ef = await dbContext.EntitlementFeatures.SingleOrDefaultAsync(
            item => item.EntitlementId == entitlement.Id && item.FeatureCode == featureCode, cancellationToken)
            ?? throw new BusinessException("FEATURE_NOT_FOUND", "Tính năng không thuộc gói hiện tại.", BusinessErrorKind.NotFound);
        existing = await FindGenericByKeyAsync(userId, FeatureValues.Adjustment, key, cancellationToken);
        if (existing is not null)
        {
            if (existing.Quantity != quantity || !string.Equals(existing.Reason, reason.Trim(), StringComparison.Ordinal))
                throw IdempotencyConflict();
            return;
        }
        if (ef.Limit is not null && Available(ef.Limit, ef.Reserved, ef.Consumed, ef.Adjustment) + quantity < 0)
            throw new BusinessException("INVALID_ADJUSTMENT", "Adjustment không thể làm quota khả dụng âm.", BusinessErrorKind.Conflict);
        var now = timeProvider.GetUtcNow();
        ef.Adjustment += quantity;
        ef.UpdatedAt = now;
        ef.ConcurrencyToken = Guid.NewGuid();
        dbContext.FeatureUsageEvents.Add(new FeatureUsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementFeatureId = ef.Id,
            FeatureCode = featureCode,
            Action = FeatureValues.Adjustment,
            Quantity = quantity,
            SourceType = "support",
            SourceId = key,
            IdempotencyKey = key,
            Reason = reason.Trim(),
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    private async Task<FeatureReservation> MapGenericReservationAsync(FeatureUsageEvent usage, string sourceId, CancellationToken cancellationToken)
    {
        if (!string.Equals(usage.SourceId, sourceId, StringComparison.Ordinal)) throw IdempotencyConflict();
        var ef = await dbContext.EntitlementFeatures.AsNoTracking().SingleAsync(item => item.Id == usage.EntitlementFeatureId, cancellationToken);
        return new FeatureReservation(usage.Id, ef.Id, Available(ef.Limit, ef.Reserved, ef.Consumed, ef.Adjustment));
    }

    private Task<FeatureUsageEvent?> FindGenericByKeyAsync(Guid userId, string action, string key, CancellationToken cancellationToken) =>
        dbContext.FeatureUsageEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.UserId == userId && item.Action == action && item.IdempotencyKey == key, cancellationToken);

    private async Task<Entitlement?> FindActiveEntitlementAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var candidates = await dbContext.Entitlements.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == BillingValues.Active).ToArrayAsync(cancellationToken);
        return candidates.Where(item => item.StartsAt <= now && item.EndsAt > now)
            .OrderBy(item => string.Equals(item.PlanCodeSnapshot, "free", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.EndsAt).FirstOrDefault();
    }

    private async Task<Entitlement?> FindActiveEntitlementForUpdateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (dbContext.Database.IsNpgsql())
            return await dbContext.Entitlements.FromSqlInterpolated(
                $"SELECT * FROM entitlements WHERE \"UserId\" = {userId} AND \"Status\" = {BillingValues.Active} AND \"StartsAt\" <= {now} AND \"EndsAt\" > {now} ORDER BY CASE WHEN LOWER(\"PlanCodeSnapshot\") = 'free' THEN 1 ELSE 0 END, \"EndsAt\" LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        var candidates = await dbContext.Entitlements.Where(item => item.UserId == userId && item.Status == BillingValues.Active).ToArrayAsync(cancellationToken);
        return candidates.Where(item => item.StartsAt <= now && item.EndsAt > now)
            .OrderBy(item => string.Equals(item.PlanCodeSnapshot, "free", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.EndsAt).FirstOrDefault();
    }

    private async Task<EntitlementFeature?> FindEntitlementFeatureForUpdateAsync(Guid entitlementFeatureId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
            return await dbContext.EntitlementFeatures.FromSqlInterpolated(
                $"SELECT * FROM entitlement_features WHERE \"Id\" = {entitlementFeatureId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        return await dbContext.EntitlementFeatures.SingleOrDefaultAsync(item => item.Id == entitlementFeatureId, cancellationToken);
    }

    private async Task<EntitlementFeature?> FindEntitlementFeatureForUpdateAsync(
        Guid entitlementId, string featureCode, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
            return await dbContext.EntitlementFeatures.FromSqlInterpolated(
                $"SELECT * FROM entitlement_features WHERE \"EntitlementId\" = {entitlementId} AND \"FeatureCode\" = {featureCode} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        return await dbContext.EntitlementFeatures.SingleOrDefaultAsync(
            item => item.EntitlementId == entitlementId && item.FeatureCode == featureCode, cancellationToken);
    }

    private async Task<IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null) return null;
        return await dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
    }

    private static async Task CommitAsync(IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is null) return;
        await transaction.CommitAsync(cancellationToken);
    }

    private static int? Available(int? limit, int reserved, int consumed, int adjustment) =>
        limit is null ? null : limit.Value + adjustment - reserved - consumed;

    private static FeatureAccessView Disabled(string code) =>
        new(code, false, null, 0, 0, 0, null, false);

    private static string RequireKey(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
            ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
            : value.Trim();

    private static string RequireSource(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160
            ? throw new BusinessException("INVALID_USAGE_SOURCE", "Usage source không hợp lệ.", BusinessErrorKind.Validation)
            : value.Trim();

    private static BusinessException FeatureUnavailable() =>
        new("FEATURE_NOT_AVAILABLE", "Tính năng này không có trong gói hiện tại.", BusinessErrorKind.Forbidden);

    private static BusinessException FeatureQuotaExceeded() =>
        new("FEATURE_QUOTA_EXCEEDED", "Bạn đã dùng hết lượt của tính năng này.", BusinessErrorKind.Forbidden);

    private static BusinessException IdempotencyConflict() =>
        new("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
}
