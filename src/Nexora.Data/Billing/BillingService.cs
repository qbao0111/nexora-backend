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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumProviderTransactionIdAttempts = 5;

    private static int? Available(int? limit, int reserved, int consumed, int adjustment) =>
        limit is null ? null : limit.Value + adjustment - reserved - consumed;
    public async Task<IReadOnlyCollection<PlanView>> GetPlansAsync(CancellationToken cancellationToken)
    {
        var plans = await dbContext.Plans.AsNoTracking().Include(item => item.Prices).ThenInclude(item => item.Features).ThenInclude(item => item.FeatureDefinition)
            .Where(plan => plan.IsActive).OrderBy(plan => plan.SortOrder).ToArrayAsync(cancellationToken);
        return plans.Select(plan => new PlanView(
            plan.Id, plan.Code, plan.Name, plan.Description ?? string.Empty, plan.Badge, plan.IsHighlighted,
            plan.Prices.Where(price => price.IsActive).OrderBy(price => price.AmountMinor).Select(price =>
            {
                var featureList = new List<PlanFeatureView>
                {
                    new(FeatureValues.Interview, "Phỏng vấn", true, price.InterviewQuota, price.InterviewQuota is null)
                };
                foreach (var f in price.Features.Where(f => !string.Equals(f.FeatureDefinition.Code, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase)))
                {
                    featureList.Add(new PlanFeatureView(f.FeatureDefinition.Code, f.FeatureDefinition.Name, f.IsEnabled, f.Limit, f.IsEnabled && f.Limit is null));
                }
                return new PlanPriceView(price.Id, price.AmountMinor, price.Currency, price.DurationDays, price.InterviewQuota, featureList.ToArray());
            }).ToArray())).ToArray();
    }

    public async Task<CheckoutSession> CreateCheckoutAsync(
        Guid userId,
        Guid planPriceId,
        string idempotencyKey,
        string? ipAddress,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = planPriceId.ToString("N");
        var existing = await FindCheckoutAsync(userId, key, fingerprint, cancellationToken);
        if (existing is not null) return await EnsureProviderCheckoutAsync(existing, ipAddress, cancellationToken);

        var price = await dbContext.PlanPrices.Include(item => item.Plan).Include(item => item.Features).ThenInclude(item => item.FeatureDefinition)
            .SingleOrDefaultAsync(item => item.Id == planPriceId && item.IsActive && item.Plan.IsActive, cancellationToken)
            ?? throw new BusinessException("PLAN_PRICE_NOT_FOUND", "Gói hoặc mức giá không còn khả dụng.", BusinessErrorKind.NotFound);
        if (price.AmountMinor == 0 || price.DurationDays is null)
            throw new BusinessException("CHECKOUT_NOT_REQUIRED", "Gói miễn phí không cần thanh toán.", BusinessErrorKind.Validation);
        var featuresSnapshot = price.Features
            .Where(feature => !string.Equals(feature.FeatureDefinition.Code, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase))
            .Select(feature => new PlanFeatureSnapshot(
                feature.FeatureDefinition.Code, feature.IsEnabled, feature.Limit)).ToArray();
        var featuresSnapshotJson = JsonSerializer.Serialize(featuresSnapshot, JsonOptions);

        for (var attempt = 0; attempt < MaximumProviderTransactionIdAttempts; attempt++)
        {
            var now = timeProvider.GetUtcNow();
            var orderId = Guid.NewGuid();
            var providerTransactionId = paymentProvider.CreateProviderTransactionId(orderId);
            if (string.IsNullOrWhiteSpace(providerTransactionId) || providerTransactionId.Length > 160)
                throw new BusinessException("PAYMENT_REFERENCE_INVALID", "Không thể tạo tham chiếu thanh toán hợp lệ.", BusinessErrorKind.ExternalFailure);

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
                FeaturesSnapshot = featuresSnapshotJson,
                Status = BillingValues.Processing,
                PaymentProvider = paymentProvider.ProviderName,
                ProviderTransactionId = providerTransactionId,
                CheckoutUrl = string.Empty,
                CreatedAt = now,
                UpdatedAt = now
            };

            var targetOrder = await TryPersistCheckoutOrderAsync(order, key, fingerprint, now, cancellationToken);
            if (targetOrder is not null)
                return await EnsureProviderCheckoutAsync(targetOrder, ipAddress, cancellationToken);
        }

        throw new BusinessException("PAYMENT_REFERENCE_COLLISION", "Không thể tạo tham chiếu thanh toán. Vui lòng thử lại.", BusinessErrorKind.Conflict);
    }

    public async Task<CheckoutStatus> GetCheckoutAsync(Guid userId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == orderId && item.UserId == userId, cancellationToken)
            ?? throw new BusinessException("ORDER_NOT_FOUND", "Không tìm thấy order thanh toán.", BusinessErrorKind.NotFound);
        return await BuildCheckoutStatusAsync(order, cancellationToken);
    }

    public async Task<CheckoutStatus> RefreshCheckoutAsync(Guid userId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == orderId && item.UserId == userId, cancellationToken)
            ?? throw new BusinessException("ORDER_NOT_FOUND", "Không tìm thấy order thanh toán.", BusinessErrorKind.NotFound);
        if (!string.Equals(order.PaymentProvider, paymentProvider.ProviderName, StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_PROVIDER_NOT_SUPPORTED", "Cổng thanh toán không được hỗ trợ.", BusinessErrorKind.NotFound);

        if (order.Status == BillingValues.Processing)
        {
            return await BuildCheckoutStatusAsync(order, cancellationToken);
        }
        if (order.Status != BillingValues.Pending) return MapCheckoutStatus(order, null);

        var verified = await paymentProvider.QueryPaymentAsync(
            new PaymentOrderRequest(order.Id, order.AmountMinor, order.Currency, order.ProviderTransactionId, order.CreatedAt, null, order.PlanCodeSnapshot), cancellationToken);
        if (verified is not null) await ApplyPaymentEventAsync(verified, cancellationToken);

        var refreshed = await dbContext.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId, cancellationToken);
        return await BuildCheckoutStatusAsync(refreshed, cancellationToken);
    }

    public async Task<PaymentWebhookProcessResult> ProcessPaymentWebhookAsync(
        string provider,
        PaymentCallbackRequest callback,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(provider, paymentProvider.ProviderName, StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_PROVIDER_NOT_SUPPORTED", "Cổng thanh toán không được hỗ trợ.", BusinessErrorKind.NotFound);
        var verified = await paymentProvider.VerifyWebhookAsync(callback, cancellationToken);
        return await ApplyPaymentEventAsync(verified, cancellationToken);
    }

    private async Task<PaymentWebhookProcessResult> ApplyPaymentEventAsync(VerifiedPaymentEvent verified, CancellationToken cancellationToken)
    {
        if (verified.IsVerificationProbe)
        {
            PaymentVerificationProbe(logger, CorrelationId(), verified.ProviderEventId);
            return new PaymentWebhookProcessResult(Guid.Empty, BillingValues.Processed, false, true);
        }

        var resolvedOrderId = await ResolveVerifiedOrderIdAsync(verified, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var duplicate = await dbContext.PaymentEvents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Provider == paymentProvider.ProviderName && item.ProviderEventId == verified.ProviderEventId, cancellationToken);
        if (duplicate is not null)
        {
            if (duplicate.OrderId != resolvedOrderId) throw IdempotencyConflict();
            PaymentDuplicate(logger, CorrelationId(), verified.ProviderEventId, duplicate.OrderId);
            var duplicateStatus = await dbContext.Orders.AsNoTracking().Where(order => order.Id == duplicate.OrderId)
                .Select(order => order.Status).SingleAsync(cancellationToken);
            return new PaymentWebhookProcessResult(duplicate.OrderId, duplicateStatus, true, duplicateStatus != BillingValues.Pending);
        }

        var order = await FindOrderForUpdateAsync(resolvedOrderId, cancellationToken)
            ?? throw new BusinessException("ORDER_NOT_FOUND", "Không tìm thấy order thanh toán.", BusinessErrorKind.NotFound);
        duplicate = await dbContext.PaymentEvents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Provider == paymentProvider.ProviderName && item.ProviderEventId == verified.ProviderEventId, cancellationToken);
        if (duplicate is not null)
        {
            if (duplicate.OrderId != resolvedOrderId) throw IdempotencyConflict();
            PaymentDuplicate(logger, CorrelationId(), verified.ProviderEventId, duplicate.OrderId);
            return new PaymentWebhookProcessResult(duplicate.OrderId, order.Status, true, order.Status != BillingValues.Pending);
        }
        if (!string.Equals(order.PaymentProvider, paymentProvider.ProviderName, StringComparison.Ordinal) ||
            !string.Equals(order.ProviderTransactionId, verified.ProviderTransactionId, StringComparison.Ordinal) ||
            !string.Equals(order.Currency, verified.Currency, StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán không khớp order.", BusinessErrorKind.Validation);
        if (order.AmountMinor != verified.AmountMinor)
            throw new BusinessException("PAYMENT_AMOUNT_MISMATCH", "Số tiền thanh toán không khớp order.", BusinessErrorKind.Validation);

        if (!verified.IsPaid && !verified.IsFinal)
        {
            await CommitAsync(transaction, cancellationToken);
            PaymentProcessed(logger, CorrelationId(), verified.ProviderEventId, order.Id, order.Status);
            return new PaymentWebhookProcessResult(order.Id, order.Status, false, order.Status != BillingValues.Pending);
        }

        var now = timeProvider.GetUtcNow();
        var wasAlreadyFinal = order.Status != BillingValues.Pending;
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
            var entitlement = new Entitlement
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
            };
            dbContext.Subscriptions.Add(subscription);
            dbContext.Entitlements.Add(entitlement);
            await SnapshotPurchasedFeaturesAsync(order, entitlement, now, cancellationToken);
            dbContext.OutboxEvents.Add(CreateOutbox("EntitlementGranted", "order", order.Id, new { order.Id, order.UserId }, now));
        }
        else if (!verified.IsPaid && verified.IsFinal && order.Status == BillingValues.Pending)
        {
            order.Status = BillingValues.Failed;
            order.UpdatedAt = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
        PaymentProcessed(logger, CorrelationId(), verified.ProviderEventId, order.Id, order.Status);
        return new PaymentWebhookProcessResult(order.Id, order.Status, false, wasAlreadyFinal);
    }

    private async Task SnapshotPurchasedFeaturesAsync(Order order, Entitlement entitlement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<PlanFeatureSnapshot> snapshots;
        try
        {
            snapshots = JsonSerializer.Deserialize<List<PlanFeatureSnapshot>>(order.FeaturesSnapshot, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Snapshot tính năng không hợp lệ.", BusinessErrorKind.Validation);
        }
        var featureDefs = await dbContext.FeatureDefinitions.AsNoTracking().Where(item => item.IsActive).ToArrayAsync(cancellationToken);
        var featureDefMap = featureDefs.ToDictionary(item => item.Code, item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in snapshots.Where(s => !string.Equals(s.Code, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase)))
        {
            if (!featureDefMap.TryGetValue(snapshot.Code, out var featureDefinitionId))
                throw new BusinessException("FEATURE_NOT_FOUND", $"Feature {snapshot.Code} không tồn tại.", BusinessErrorKind.Validation);
            dbContext.EntitlementFeatures.Add(new EntitlementFeature
            {
                Id = Guid.NewGuid(),
                EntitlementId = entitlement.Id,
                FeatureDefinitionId = featureDefinitionId,
                FeatureCode = snapshot.Code,
                IsEnabled = snapshot.Enabled,
                Limit = snapshot.Limit,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid()
            });
        }
    }

    public async Task<BillingSummary> GetSummaryAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var isSqlite = string.Equals(
            dbContext.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.Sqlite",
            StringComparison.Ordinal);
        var activeEntitlements = await dbContext.Entitlements.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == BillingValues.Active)
            .Select(item => new
            {
                item.Id,
                item.PlanCodeSnapshot,
                item.Status,
                item.InterviewLimit,
                item.Reserved,
                item.Consumed,
                item.Adjustment,
                item.StartsAt,
                item.EndsAt
            })
            .ToArrayAsync(cancellationToken);
        var entitlementIds = activeEntitlements.Select(item => item.Id).ToArray();
        var featureRows = await dbContext.EntitlementFeatures.AsNoTracking()
            .Where(item => entitlementIds.Contains(item.EntitlementId))
            .Select(item => new
            {
                item.EntitlementId,
                item.FeatureCode,
                item.FeatureDefinitionId,
                item.IsEnabled,
                item.Limit,
                item.Reserved,
                item.Consumed,
                item.Adjustment
            })
            .ToArrayAsync(cancellationToken);
        var views = new List<EntitlementView>();
        var featureDefNames = await dbContext.FeatureDefinitions.AsNoTracking().ToDictionaryAsync(item => item.Id, item => item.Name, cancellationToken);
        foreach (var entry in activeEntitlements)
        {
            var interview = new EntitlementFeatureView(FeatureValues.Interview, "Phỏng vấn",
                true, entry.InterviewLimit, entry.Reserved, entry.Consumed, entry.Adjustment,
                Available(entry.InterviewLimit, entry.Reserved, entry.Consumed, entry.Adjustment),
                entry.InterviewLimit is null);
            var generic = featureRows
                .Where(ef => ef.EntitlementId == entry.Id)
                .Where(ef => !string.Equals(ef.FeatureCode, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase))
                .Select(ef => new EntitlementFeatureView(
                    ef.FeatureCode, featureDefNames.GetValueOrDefault(ef.FeatureDefinitionId, ef.FeatureCode), ef.IsEnabled, ef.Limit,
                    ef.Reserved, ef.Consumed, ef.Adjustment, Available(ef.Limit, ef.Reserved, ef.Consumed, ef.Adjustment), ef.IsEnabled && ef.Limit is null)).ToArray();
            views.Add(new EntitlementView(entry.Id, entry.PlanCodeSnapshot, entry.Status, entry.StartsAt, entry.EndsAt,
                entry.InterviewLimit, entry.Reserved, entry.Consumed, entry.Adjustment, [interview, .. generic]));
        }
        var entitlement = views.Where(item => item.StartsAt <= now && item.EndsAt > now)
            .OrderBy(item => string.Equals(item.PlanCode, "free", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.EndsAt).FirstOrDefault();
        var orderRowsQuery = dbContext.Orders.AsNoTracking()
            .Where(order => order.UserId == userId)
            .Select(order => new { order.Id, order.PlanCodeSnapshot, order.AmountMinor, order.Currency, order.Status, order.CreatedAt });
        var orderRows = isSqlite
            ? await dbContext.Orders
                .FromSqlInterpolated($"SELECT * FROM orders WHERE \"UserId\" = {userId} ORDER BY \"CreatedAt\" DESC, \"Id\" DESC LIMIT 20")
                .AsNoTracking()
                .Select(order => new { order.Id, order.PlanCodeSnapshot, order.AmountMinor, order.Currency, order.Status, order.CreatedAt })
                .ToArrayAsync(cancellationToken)
            : await orderRowsQuery
                .OrderByDescending(order => order.CreatedAt)
                .ThenByDescending(order => order.Id)
                .Take(20)
                .ToArrayAsync(cancellationToken);
        var orders = orderRows
            .Select(order => new OrderView(order.Id, order.PlanCodeSnapshot, order.AmountMinor, order.Currency, order.Status, order.CreatedAt))
            .ToArray();
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
        await CommitAsync(transaction, cancellationToken);
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
        await CommitAsync(transaction, cancellationToken);
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
        await CommitAsync(transaction, cancellationToken);
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

    private async Task<Order?> TryPersistCheckoutOrderAsync(
        Order order,
        string idempotencyKey,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            var existing = await FindCheckoutAsync(order.UserId, idempotencyKey, fingerprint, cancellationToken);
            if (existing is not null) return existing;

            var providerReferenceExists = await dbContext.Orders.AsNoTracking().AnyAsync(item =>
                item.PaymentProvider == order.PaymentProvider &&
                item.ProviderTransactionId == order.ProviderTransactionId,
                cancellationToken);
            if (providerReferenceExists) return null;

            dbContext.Orders.Add(order);
            dbContext.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                ActorId = order.UserId,
                Operation = "checkout.create",
                Key = idempotencyKey,
                RequestFingerprint = fingerprint,
                ResourceId = order.Id,
                CreatedAt = now
            });
            dbContext.OutboxEvents.Add(CreateOutbox("CheckoutCreated", "order", order.Id, new { order.Id, order.UserId }, now));

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                await CommitAsync(transaction, cancellationToken);
                return order;
            }
            catch (DbUpdateException)
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();

                existing = await FindCheckoutAsync(order.UserId, idempotencyKey, fingerprint, cancellationToken);
                if (existing is not null) return existing;

                providerReferenceExists = await dbContext.Orders.AsNoTracking().AnyAsync(item =>
                    item.PaymentProvider == order.PaymentProvider &&
                    item.ProviderTransactionId == order.ProviderTransactionId,
                    cancellationToken);
                if (providerReferenceExists) return null;
                throw;
            }
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private async Task<CheckoutSession> EnsureProviderCheckoutAsync(Order order, string? ipAddress, CancellationToken cancellationToken)
    {
        if (!string.Equals(order.PaymentProvider, paymentProvider.ProviderName, StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_PROVIDER_NOT_SUPPORTED", "Cổng thanh toán không được hỗ trợ.", BusinessErrorKind.NotFound);
        if (order.Status is not (BillingValues.Processing or BillingValues.Pending)) return MapCheckout(order, null);

        var transaction = await BeginTransactionAsync(cancellationToken);
        try
        {
            var current = await FindOrderForUpdateAsync(order.Id, cancellationToken)
                ?? throw new BusinessException("ORDER_NOT_FOUND", "Không tìm thấy order thanh toán.", BusinessErrorKind.NotFound);
            if (current.Status is not (BillingValues.Processing or BillingValues.Pending)) return MapCheckout(current, null);

            var persistedAction = RestorePersistedCheckoutAction(current);
            if (persistedAction is not null)
            {
                if (string.IsNullOrWhiteSpace(current.CheckoutActionSnapshot))
                {
                    current.CheckoutActionSnapshot = SerializeCheckoutAction(persistedAction);
                    current.UpdatedAt = timeProvider.GetUtcNow();
                    await dbContext.SaveChangesAsync(cancellationToken);
                    await CommitAsync(transaction, cancellationToken);
                }
                return MapCheckout(current, persistedAction);
            }

            var providerCheckout = await paymentProvider.CreateCheckoutAsync(
                new PaymentOrderRequest(current.Id, current.AmountMinor, current.Currency, current.ProviderTransactionId, current.CreatedAt, ipAddress, current.PlanCodeSnapshot), cancellationToken);
            if (!string.Equals(providerCheckout.Provider, paymentProvider.ProviderName, StringComparison.Ordinal) ||
                !string.Equals(providerCheckout.ProviderTransactionId, current.ProviderTransactionId, StringComparison.Ordinal) ||
                !IsValidCheckoutAction(providerCheckout.Action))
                throw new BusinessException("PAYMENT_CHECKOUT_FAILED", "Không thể tạo phiên thanh toán.", BusinessErrorKind.ExternalFailure);

            current.Status = BillingValues.Pending;
            current.CheckoutUrl = providerCheckout.Action.Url;
            current.CheckoutActionSnapshot = SerializeCheckoutAction(providerCheckout.Action);
            current.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapCheckout(current, providerCheckout.Action);
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }
    }

    private async Task<CheckoutStatus> BuildCheckoutStatusAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.Status is not (BillingValues.Processing or BillingValues.Pending)) return MapCheckoutStatus(order, null);
        var checkout = await EnsureProviderCheckoutAsync(order, null, cancellationToken);
        var current = await dbContext.Orders.AsNoTracking().SingleAsync(item => item.Id == order.Id, cancellationToken);
        return MapCheckoutStatus(current, checkout.Checkout);
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
                $"SELECT * FROM entitlements WHERE \"UserId\" = {userId} AND \"Status\" = {BillingValues.Active} AND \"StartsAt\" <= {now} AND \"EndsAt\" > {now} ORDER BY CASE WHEN LOWER(\"PlanCodeSnapshot\") = 'free' THEN 1 ELSE 0 END, \"EndsAt\" LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        var candidates = await dbContext.Entitlements.Where(item => item.UserId == userId && item.Status == BillingValues.Active)
            .ToArrayAsync(cancellationToken);
        return candidates.Where(item => item.StartsAt <= now && item.EndsAt > now)
            .OrderBy(item => string.Equals(item.PlanCodeSnapshot, "free", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.EndsAt).FirstOrDefault();
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

    private async Task<Guid> ResolveVerifiedOrderIdAsync(VerifiedPaymentEvent verified, CancellationToken cancellationToken)
    {
        if (verified.OrderId is { } orderId && orderId != Guid.Empty) return orderId;
        var resolvedOrderId = await dbContext.Orders.AsNoTracking()
            .Where(order => order.PaymentProvider == paymentProvider.ProviderName &&
                order.ProviderTransactionId == verified.ProviderTransactionId)
            .Select(order => order.Id)
            .SingleOrDefaultAsync(cancellationToken);
        return resolvedOrderId == Guid.Empty
            ? throw new BusinessException("ORDER_NOT_FOUND", "Không tìm thấy order thanh toán.", BusinessErrorKind.NotFound)
            : resolvedOrderId;
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null) return null;
        return await dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
    }

    private static async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is null) return;
        await transaction.CommitAsync(cancellationToken);
    }

    private static int? Available(Entitlement entitlement) =>
        entitlement.InterviewLimit is null ? null : entitlement.InterviewLimit.Value + entitlement.Adjustment - entitlement.Reserved - entitlement.Consumed;

    private CheckoutAction? RestorePersistedCheckoutAction(Order order)
    {
        if (order.Status != BillingValues.Pending || string.IsNullOrWhiteSpace(order.CheckoutUrl)) return null;

        if (!string.IsNullOrWhiteSpace(order.CheckoutActionSnapshot))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<CheckoutActionSnapshot>(order.CheckoutActionSnapshot, JsonOptions);
                if (snapshot?.Fields is not null)
                {
                    var action = new CheckoutAction(snapshot.Method ?? string.Empty, snapshot.Url ?? string.Empty, snapshot.Fields);
                    if (IsValidCheckoutAction(action) && string.Equals(action.Url, order.CheckoutUrl, StringComparison.Ordinal))
                        return action;
                }
            }
            catch (JsonException)
            {
                // A legacy/corrupt snapshot is not trusted. The provider compatibility fallback below
                // may still restore a redirect-only checkout without creating a second payment link.
            }
        }

        var restored = paymentProvider.RestoreCheckoutAction(order.CheckoutUrl);
        if (restored is null || !IsValidCheckoutAction(restored)) return null;
        return string.Equals(restored.Url, order.CheckoutUrl, StringComparison.Ordinal) ? restored : null;
    }

    private static string SerializeCheckoutAction(CheckoutAction action) =>
        JsonSerializer.Serialize(new CheckoutActionSnapshot(action.Method, action.Url, action.Fields.ToArray()), JsonOptions);

    private static bool IsValidCheckoutAction(CheckoutAction? action) =>
        action is not null &&
        !string.IsNullOrWhiteSpace(action.Method) &&
        !string.IsNullOrWhiteSpace(action.Url) &&
        action.Fields is not null &&
        action.Fields.All(field => field is not null && !string.IsNullOrWhiteSpace(field.Name) && field.Value is not null);

    private static CheckoutSession MapCheckout(Order order, CheckoutAction? action) =>
        new(order.Id, order.Status, order.AmountMinor, order.Currency, order.PaymentProvider, action);

    private static CheckoutStatus MapCheckoutStatus(Order order, CheckoutAction? action) =>
        new(order.Id, order.PlanCodeSnapshot, order.AmountMinor, order.Currency, order.PaymentProvider, order.Status, action, order.CreatedAt, order.UpdatedAt);

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

    private sealed record CheckoutActionSnapshot(string? Method, string? Url, CheckoutFormField[]? Fields);

    [LoggerMessage(LogLevel.Information,
        "Payment event {ProviderEventId} for order {OrderId} processed with status {OrderStatus}; correlation {CorrelationId}")]
    private static partial void PaymentProcessed(
        ILogger logger, string correlationId, string providerEventId, Guid orderId, string orderStatus);

    [LoggerMessage(LogLevel.Information,
        "Duplicate payment event {ProviderEventId} for order {OrderId} ignored; correlation {CorrelationId}")]
    private static partial void PaymentDuplicate(ILogger logger, string correlationId, string providerEventId, Guid orderId);

    [LoggerMessage(LogLevel.Information,
        "Verified payment-provider probe {ProviderEventId} ignored; correlation {CorrelationId}")]
    private static partial void PaymentVerificationProbe(ILogger logger, string correlationId, string providerEventId);

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
