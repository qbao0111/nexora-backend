using System.Data;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Data.Persistence;

namespace Nexora.Data.Billing;

public sealed partial class BillingService(
    NexoraDbContext dbContext,
    IPaymentProvider paymentProvider,
    TimeProvider timeProvider,
    ILogger<BillingService> logger) : IBillingService
{
    public async Task<IReadOnlyCollection<PlanView>> GetPlansAsync(CancellationToken cancellationToken) =>
        await dbContext.Plans.AsNoTracking().Where(plan => plan.IsActive).OrderBy(plan => plan.SortOrder)
            .Select(plan => new PlanView(
                plan.Id,
                plan.Code,
                plan.Name,
                plan.Prices.Where(price => price.IsActive).OrderBy(price => price.AmountMinor)
                    .Select(price => new PlanPriceView(price.Id, price.AmountMinor, price.Currency, price.DurationDays, price.InterviewQuota)).ToArray()))
            .ToArrayAsync(cancellationToken);

    public async Task<CheckoutSession> CreateCheckoutAsync(
        Guid userId,
        Guid planPriceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = planPriceId.ToString("N");
        var existing = await FindCheckoutAsync(userId, key, fingerprint, cancellationToken);
        if (existing is not null) return MapCheckout(existing);

        var price = await dbContext.PlanPrices.Include(item => item.Plan)
            .SingleOrDefaultAsync(item => item.Id == planPriceId && item.IsActive && item.Plan.IsActive, cancellationToken)
            ?? throw new BusinessException("PLAN_PRICE_NOT_FOUND", "Gói hoặc mức giá không còn khả dụng.", BusinessErrorKind.NotFound);
        if (price.AmountMinor == 0 || price.DurationDays is null)
            throw new BusinessException("CHECKOUT_NOT_REQUIRED", "Gói miễn phí không cần thanh toán.", BusinessErrorKind.Validation);
        var now = timeProvider.GetUtcNow();
        var orderId = Guid.NewGuid();
        var providerCheckout = await paymentProvider.CreateCheckoutAsync(
            new PaymentOrderRequest(orderId, price.AmountMinor, price.Currency), cancellationToken);
        var order = new Order
        {
            Id = orderId,
            UserId = userId,
            PlanPriceId = price.Id,
            PlanCodeSnapshot = price.Plan.Code,
            AmountMinor = price.AmountMinor,
            Currency = price.Currency,
            DurationDays = price.DurationDays,
            InterviewQuota = price.InterviewQuota,
            Status = BillingValues.Pending,
            PaymentProvider = providerCheckout.Provider,
            ProviderTransactionId = providerCheckout.ProviderTransactionId,
            CheckoutUrl = providerCheckout.CheckoutUrl,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        existing = await FindCheckoutAsync(userId, key, fingerprint, cancellationToken);
        if (existing is not null) return MapCheckout(existing);
        dbContext.Orders.Add(order);
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Operation = "checkout.create",
            Key = key,
            RequestFingerprint = fingerprint,
            ResourceId = order.Id,
            CreatedAt = now
        });
        dbContext.OutboxEvents.Add(CreateOutbox("CheckoutCreated", "order", order.Id, new { order.Id, order.UserId }, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            existing = await FindCheckoutAsync(userId, key, fingerprint, cancellationToken);
            if (existing is not null) return MapCheckout(existing);
            throw;
        }
        await transaction.CommitAsync(cancellationToken);
        return MapCheckout(order);
    }

    public async Task<string> ProcessPaymentWebhookAsync(
        string provider,
        string signature,
        string timestamp,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(provider, paymentProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_PROVIDER_NOT_SUPPORTED", "Cổng thanh toán không được hỗ trợ.", BusinessErrorKind.NotFound);
        var verified = await paymentProvider.VerifyWebhookAsync(signature, timestamp, body, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var duplicate = await dbContext.PaymentEvents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Provider == paymentProvider.ProviderName && item.ProviderEventId == verified.ProviderEventId, cancellationToken);
        if (duplicate is not null)
        {
            if (duplicate.OrderId != verified.OrderId) throw IdempotencyConflict();
            PaymentDuplicate(logger, CorrelationId(), verified.ProviderEventId, duplicate.OrderId);
            return await dbContext.Orders.Where(order => order.Id == duplicate.OrderId).Select(order => order.Status).SingleAsync(cancellationToken);
        }

        var order = await FindOrderForUpdateAsync(verified.OrderId, cancellationToken)
            ?? throw new BusinessException("ORDER_NOT_FOUND", "Không tìm thấy order thanh toán.", BusinessErrorKind.NotFound);
        duplicate = await dbContext.PaymentEvents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Provider == paymentProvider.ProviderName && item.ProviderEventId == verified.ProviderEventId, cancellationToken);
        if (duplicate is not null)
        {
            if (duplicate.OrderId != verified.OrderId) throw IdempotencyConflict();
            PaymentDuplicate(logger, CorrelationId(), verified.ProviderEventId, duplicate.OrderId);
            return order.Status;
        }
        if (!string.Equals(order.PaymentProvider, paymentProvider.ProviderName, StringComparison.Ordinal) ||
            !string.Equals(order.ProviderTransactionId, verified.ProviderTransactionId, StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán không khớp order.", BusinessErrorKind.Validation);

        var now = timeProvider.GetUtcNow();
        dbContext.PaymentEvents.Add(new PaymentEvent
        {
            Id = Guid.NewGuid(),
            OrderId = order.Id,
            Provider = paymentProvider.ProviderName,
            ProviderEventId = verified.ProviderEventId,
            OccurredAt = verified.OccurredAt,
            ReceivedAt = now
        });
        if (verified.IsPaid && order.Status == BillingValues.Pending)
        {
            order.Status = BillingValues.Fulfilled;
            order.UpdatedAt = now;
            var subscription = new Subscription
            {
                Id = Guid.NewGuid(),
                UserId = order.UserId,
                OrderId = order.Id,
                Status = BillingValues.Active,
                StartsAt = now,
                EndsAt = now.AddDays(order.DurationDays!.Value),
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.Subscriptions.Add(subscription);
            dbContext.Entitlements.Add(new Entitlement
            {
                Id = Guid.NewGuid(),
                UserId = order.UserId,
                SubscriptionId = subscription.Id,
                PlanCodeSnapshot = order.PlanCodeSnapshot,
                Status = BillingValues.Active,
                InterviewLimit = order.InterviewQuota,
                StartsAt = subscription.StartsAt,
                EndsAt = subscription.EndsAt,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid()
            });
            dbContext.OutboxEvents.Add(CreateOutbox("EntitlementGranted", "order", order.Id, new { order.Id, order.UserId }, now));
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PaymentProcessed(logger, CorrelationId(), verified.ProviderEventId, order.Id, order.Status);
        return order.Status;
    }

    public async Task<BillingSummary> GetSummaryAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var activeEntitlements = await dbContext.Entitlements.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == BillingValues.Active)
            .Select(item => new EntitlementView(
                item.Id,
                item.PlanCodeSnapshot,
                item.Status,
                item.StartsAt,
                item.EndsAt,
                item.InterviewLimit,
                item.Reserved,
                item.Consumed,
                item.Adjustment))
            .ToArrayAsync(cancellationToken);
        var entitlement = activeEntitlements.Where(item => item.StartsAt <= now && item.EndsAt > now)
            .OrderBy(item => item.EndsAt).FirstOrDefault();
        var orderRows = await dbContext.Orders.AsNoTracking().Where(order => order.UserId == userId)
            .Select(order => new OrderView(order.Id, order.PlanCodeSnapshot, order.AmountMinor, order.Currency, order.Status, order.CreatedAt))
            .ToArrayAsync(cancellationToken);
        var orders = orderRows.OrderByDescending(order => order.CreatedAt).Take(20).ToArray();
        return new BillingSummary(entitlement, orders);
    }

    public async Task<UsageReservation> ReserveInterviewAsync(
        Guid userId,
        string sourceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var source = RequireSource(sourceId);
        var key = RequireKey(idempotencyKey);
        var existing = await FindUsageByKeyAsync(userId, BillingValues.Reserve, key, cancellationToken);
        if (existing is not null) return await MapExistingReservationAsync(existing, source, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        existing = await FindUsageByKeyAsync(userId, BillingValues.Reserve, key, cancellationToken);
        if (existing is not null) return await MapExistingReservationAsync(existing, source, cancellationToken);
        var entitlement = await FindActiveEntitlementForUpdateAsync(userId, cancellationToken) ?? throw QuotaExceeded();
        existing = await FindUsageByKeyAsync(userId, BillingValues.Reserve, key, cancellationToken);
        if (existing is not null) return await MapExistingReservationAsync(existing, source, cancellationToken);
        if (Available(entitlement) < 1) throw QuotaExceeded();
        var now = timeProvider.GetUtcNow();
        var usage = new UsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementId = entitlement.Id,
            Action = BillingValues.Reserve,
            Quantity = 1,
            SourceType = "interview",
            SourceId = source,
            IdempotencyKey = key,
            CreatedAt = now
        };
        entitlement.Reserved++;
        entitlement.UpdatedAt = now;
        entitlement.ConcurrencyToken = Guid.NewGuid();
        dbContext.UsageEvents.Add(usage);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new UsageReservation(usage.Id, entitlement.Id, Available(entitlement));
    }

    public Task ConsumeReservationAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken) =>
        CompleteReservationAsync(userId, reservationEventId, BillingValues.Consume, cancellationToken);

    public Task VoidReservationAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken) =>
        CompleteReservationAsync(userId, reservationEventId, BillingValues.Void, cancellationToken);

    public async Task AdjustInterviewQuotaAsync(
        Guid userId,
        int quantity,
        string reason,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (quantity == 0 || string.IsNullOrWhiteSpace(reason))
            throw new BusinessException("INVALID_ADJUSTMENT", "Adjustment cần số lượng khác 0 và lý do.", BusinessErrorKind.Validation);
        var key = RequireKey(idempotencyKey);
        var existingAdjustment = await FindUsageByKeyAsync(userId, BillingValues.Adjustment, key, cancellationToken);
        if (existingAdjustment is not null)
        {
            EnsureEquivalentAdjustment(existingAdjustment, quantity, reason);
            return;
        }
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        existingAdjustment = await FindUsageByKeyAsync(userId, BillingValues.Adjustment, key, cancellationToken);
        if (existingAdjustment is not null)
        {
            EnsureEquivalentAdjustment(existingAdjustment, quantity, reason);
            return;
        }
        var entitlement = await FindActiveEntitlementForUpdateAsync(userId, cancellationToken) ?? throw QuotaExceeded();
        existingAdjustment = await FindUsageByKeyAsync(userId, BillingValues.Adjustment, key, cancellationToken);
        if (existingAdjustment is not null)
        {
            EnsureEquivalentAdjustment(existingAdjustment, quantity, reason);
            return;
        }
        if (Available(entitlement) + quantity < 0)
            throw new BusinessException("INVALID_ADJUSTMENT", "Adjustment không thể làm quota khả dụng âm.", BusinessErrorKind.Conflict);
        var now = timeProvider.GetUtcNow();
        entitlement.Adjustment += quantity;
        entitlement.UpdatedAt = now;
        entitlement.ConcurrencyToken = Guid.NewGuid();
        dbContext.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementId = entitlement.Id,
            Action = BillingValues.Adjustment,
            Quantity = quantity,
            SourceType = "support",
            SourceId = key,
            IdempotencyKey = key,
            Reason = reason.Trim(),
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task CompleteReservationAsync(Guid userId, Guid reservationEventId, string action, CancellationToken cancellationToken)
    {
        var sourceId = reservationEventId.ToString("N");
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var reservation = await dbContext.UsageEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == reservationEventId && item.UserId == userId && item.Action == BillingValues.Reserve, cancellationToken)
            ?? throw new BusinessException("RESERVATION_NOT_FOUND", "Không tìm thấy reservation.", BusinessErrorKind.NotFound);
        var existing = await dbContext.UsageEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.EntitlementId == reservation.EntitlementId && item.SourceId == sourceId &&
                (item.Action == BillingValues.Consume || item.Action == BillingValues.Void), cancellationToken);
        if (existing is not null)
        {
            if (existing.Action == action) return;
            throw new BusinessException("RESERVATION_ALREADY_FINALIZED", "Reservation đã được xử lý.", BusinessErrorKind.Conflict);
        }
        var entitlement = await FindEntitlementForUpdateAsync(reservation.EntitlementId, cancellationToken)
            ?? throw new BusinessException("ENTITLEMENT_NOT_FOUND", "Không tìm thấy entitlement.", BusinessErrorKind.NotFound);
        existing = await dbContext.UsageEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.EntitlementId == reservation.EntitlementId && item.SourceId == sourceId &&
                (item.Action == BillingValues.Consume || item.Action == BillingValues.Void), cancellationToken);
        if (existing is not null)
        {
            if (existing.Action == action) return;
            throw new BusinessException("RESERVATION_ALREADY_FINALIZED", "Reservation đã được xử lý.", BusinessErrorKind.Conflict);
        }
        if (entitlement.Reserved < reservation.Quantity)
            throw new BusinessException("INVALID_USAGE_STATE", "Usage ledger không ở trạng thái hợp lệ.", BusinessErrorKind.Conflict);
        var now = timeProvider.GetUtcNow();
        entitlement.Reserved -= reservation.Quantity;
        if (action == BillingValues.Consume) entitlement.Consumed += reservation.Quantity;
        entitlement.UpdatedAt = now;
        entitlement.ConcurrencyToken = Guid.NewGuid();
        dbContext.UsageEvents.Add(new UsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementId = entitlement.Id,
            Action = action,
            Quantity = reservation.Quantity,
            SourceType = "reservation",
            SourceId = sourceId,
            IdempotencyKey = $"{action}:{sourceId}",
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<Order?> FindCheckoutAsync(Guid userId, string key, string fingerprint, CancellationToken cancellationToken)
    {
        var record = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "checkout.create" && item.Key == key, cancellationToken);
        if (record is null) return null;
        if (!string.Equals(record.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
        return await dbContext.Orders.AsNoTracking().SingleAsync(order => order.Id == record.ResourceId, cancellationToken);
    }

    private Task<UsageEvent?> FindUsageByKeyAsync(Guid userId, string action, string key, CancellationToken cancellationToken) =>
        dbContext.UsageEvents.AsNoTracking().SingleOrDefaultAsync(
            item => item.UserId == userId && item.Action == action && item.IdempotencyKey == key, cancellationToken);

    private async Task<UsageReservation> MapReservationAsync(UsageEvent usage, CancellationToken cancellationToken)
    {
        var available = await dbContext.Entitlements.Where(item => item.Id == usage.EntitlementId)
            .Select(item => item.InterviewLimit + item.Adjustment - item.Reserved - item.Consumed).SingleAsync(cancellationToken);
        return new UsageReservation(usage.Id, usage.EntitlementId, available);
    }

    private Task<UsageReservation> MapExistingReservationAsync(UsageEvent usage, string sourceId, CancellationToken cancellationToken)
    {
        if (!string.Equals(usage.SourceId, sourceId, StringComparison.Ordinal))
            throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
        return MapReservationAsync(usage, cancellationToken);
    }

    private async Task<Entitlement?> FindActiveEntitlementForUpdateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        if (dbContext.Database.IsNpgsql())
            return await dbContext.Entitlements.FromSqlInterpolated(
                $"SELECT * FROM entitlements WHERE \"UserId\" = {userId} AND \"Status\" = {BillingValues.Active} AND \"StartsAt\" <= {now} AND \"EndsAt\" > {now} ORDER BY \"EndsAt\" LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        var candidates = await dbContext.Entitlements.Where(item => item.UserId == userId && item.Status == BillingValues.Active)
            .ToArrayAsync(cancellationToken);
        return candidates.Where(item => item.StartsAt <= now && item.EndsAt > now).OrderBy(item => item.EndsAt).FirstOrDefault();
    }

    private async Task<Entitlement?> FindEntitlementForUpdateAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
            return await dbContext.Entitlements.FromSqlInterpolated(
                $"SELECT * FROM entitlements WHERE \"Id\" = {entitlementId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        return await dbContext.Entitlements.SingleOrDefaultAsync(item => item.Id == entitlementId, cancellationToken);
    }

    private async Task<Order?> FindOrderForUpdateAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
            return await dbContext.Orders.FromSqlInterpolated(
                $"SELECT * FROM orders WHERE \"Id\" = {orderId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        return await dbContext.Orders.SingleOrDefaultAsync(order => order.Id == orderId, cancellationToken);
    }

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);

    private static int? Available(Entitlement entitlement) =>
        entitlement.InterviewLimit is null ? null : entitlement.InterviewLimit.Value + entitlement.Adjustment - entitlement.Reserved - entitlement.Consumed;

    private static CheckoutSession MapCheckout(Order order) =>
        new(order.Id, order.Status, order.AmountMinor, order.Currency, order.PaymentProvider, order.CheckoutUrl);

    private static OutboxEvent CreateOutbox(string type, string aggregateType, Guid aggregateId, object payload, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        AggregateType = aggregateType,
        AggregateId = aggregateId,
        Payload = JsonSerializer.Serialize(payload),
        Status = BillingValues.Pending,
        CreatedAt = now
    };

    private static string CorrelationId() => Activity.Current?.TraceId.ToString() ?? "none";

    [LoggerMessage(LogLevel.Information,
        "Payment event {ProviderEventId} for order {OrderId} processed with status {OrderStatus}; correlation {CorrelationId}")]
    private static partial void PaymentProcessed(
        ILogger logger, string correlationId, string providerEventId, Guid orderId, string orderStatus);

    [LoggerMessage(LogLevel.Information,
        "Duplicate payment event {ProviderEventId} for order {OrderId} ignored; correlation {CorrelationId}")]
    private static partial void PaymentDuplicate(ILogger logger, string correlationId, string providerEventId, Guid orderId);

    private static string RequireKey(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
            ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
            : value.Trim();

    private static string RequireSource(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > 160
            ? throw new BusinessException("INVALID_USAGE_SOURCE", "Usage source không hợp lệ.", BusinessErrorKind.Validation)
            : value.Trim();

    private static void EnsureEquivalentAdjustment(UsageEvent usage, int quantity, string reason)
    {
        if (usage.Quantity != quantity || !string.Equals(usage.Reason, reason.Trim(), StringComparison.Ordinal)) throw IdempotencyConflict();
    }

    private static BusinessException IdempotencyConflict() =>
        new("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);

    private static BusinessException QuotaExceeded() =>
        new("QUOTA_EXCEEDED", "Bạn đã dùng hết lượt phỏng vấn của gói hiện tại.", BusinessErrorKind.Forbidden);
}
