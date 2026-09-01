namespace Nexora.Business.Billing;

public static class BillingValues
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Processed = "processed";
    public const string Fulfilled = "fulfilled";
    public const string Active = "active";
    public const string Reserve = "reserve";
    public const string Consume = "consume";
    public const string Void = "void";
    public const string Adjustment = "adjustment";
}

public sealed record PlanPriceView(
    Guid Id,
    long AmountMinor,
    string Currency,
    int? DurationDays,
    int? InterviewQuota,
    IReadOnlyCollection<PlanFeatureView> Features);

public sealed record PlanView(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string? Badge,
    bool IsHighlighted,
    IReadOnlyCollection<PlanPriceView> Prices);

public sealed record CheckoutSession(
    Guid OrderId,
    string Status,
    long AmountMinor,
    string Currency,
    string Provider,
    string CheckoutUrl);

public sealed record CheckoutStatus(
    Guid OrderId,
    string PlanCode,
    long AmountMinor,
    string Currency,
    string Provider,
    string Status,
    string CheckoutUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record OrderView(Guid Id, string PlanCode, long AmountMinor, string Currency, string Status, DateTimeOffset CreatedAt);
public sealed record BillingSummary(EntitlementView? Entitlement, IReadOnlyCollection<OrderView> Orders);
public sealed record EntitlementView(
    Guid Id,
    string PlanCode,
    string Status,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    int? Limit,
    int Reserved,
    int Consumed,
    int Adjustment,
    IReadOnlyCollection<EntitlementFeatureView> Features)
{
    public int? Available => Limit is null ? null : Math.Max(0, Limit.Value + Adjustment - Reserved - Consumed);
}
public sealed record UsageReservation(Guid EventId, Guid EntitlementId, int? Available);

public interface IBillingService
{
    Task<IReadOnlyCollection<PlanView>> GetPlansAsync(CancellationToken cancellationToken);
    Task<CheckoutSession> CreateCheckoutAsync(Guid userId, Guid planPriceId, string idempotencyKey, CancellationToken cancellationToken);
    Task<CheckoutStatus> GetCheckoutAsync(Guid userId, Guid orderId, CancellationToken cancellationToken);
    Task<CheckoutStatus> RefreshCheckoutAsync(Guid userId, Guid orderId, CancellationToken cancellationToken);
    Task<string> ProcessPaymentWebhookAsync(string provider, string signature, string timestamp, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
    Task<BillingSummary> GetSummaryAsync(Guid userId, CancellationToken cancellationToken);
    Task<UsageReservation> ReserveInterviewAsync(Guid userId, string sourceId, string idempotencyKey, CancellationToken cancellationToken);
    Task ConsumeReservationAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken);
    Task VoidReservationAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken);
    Task AdjustInterviewQuotaAsync(Guid userId, int quantity, string reason, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed record PaymentOrderRequest(Guid OrderId, long AmountMinor, string Currency, string ProviderTransactionId);
public sealed record PaymentCheckout(string Provider, string ProviderTransactionId, string CheckoutUrl);
public sealed record VerifiedPaymentEvent(string ProviderEventId, Guid OrderId, string ProviderTransactionId, long AmountMinor, string Currency, bool IsPaid, DateTimeOffset OccurredAt);

public interface IPaymentProvider
{
    string ProviderName { get; }
    string CreateProviderTransactionId(Guid orderId);
    Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken);
    Task<VerifiedPaymentEvent> VerifyWebhookAsync(string signature, string timestamp, ReadOnlyMemory<byte> body, CancellationToken cancellationToken);
    Task<VerifiedPaymentEvent?> QueryPaymentAsync(PaymentOrderRequest request, CancellationToken cancellationToken);
}
