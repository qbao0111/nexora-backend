namespace Nexora.Business.Billing;

public static class BillingValues
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Processed = "processed";
    public const string Fulfilled = "fulfilled";
    public const string Failed = "failed";
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
    CheckoutAction? Checkout);

public sealed record CheckoutStatus(
    Guid OrderId,
    string PlanCode,
    long AmountMinor,
    string Currency,
    string Provider,
    string Status,
    CheckoutAction? Checkout,
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
    Task<CheckoutSession> CreateCheckoutAsync(Guid userId, Guid planPriceId, string idempotencyKey, string? ipAddress, CancellationToken cancellationToken);
    Task<CheckoutStatus> GetCheckoutAsync(Guid userId, Guid orderId, CancellationToken cancellationToken);
    Task<CheckoutStatus> RefreshCheckoutAsync(Guid userId, Guid orderId, CancellationToken cancellationToken);
    Task<PaymentWebhookProcessResult> ProcessPaymentWebhookAsync(string provider, PaymentCallbackRequest callback, CancellationToken cancellationToken);
    Task<BillingSummary> GetSummaryAsync(Guid userId, CancellationToken cancellationToken);
    Task<UsageReservation> ReserveInterviewAsync(Guid userId, string sourceId, string idempotencyKey, CancellationToken cancellationToken);
    Task ConsumeReservationAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken);
    Task VoidReservationAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken);
    Task AdjustInterviewQuotaAsync(Guid userId, int quantity, string reason, string idempotencyKey, CancellationToken cancellationToken);
}

public sealed record PaymentOrderRequest(
    Guid OrderId,
    long AmountMinor,
    string Currency,
    string ProviderTransactionId,
    DateTimeOffset CreatedAt,
    string? IpAddress,
    string? PlanCode = null);
public sealed record CheckoutFormField(string Name, string Value);
public sealed record CheckoutAction(string Method, string Url, IReadOnlyList<CheckoutFormField> Fields);
public sealed record PaymentCheckout(string Provider, string ProviderTransactionId, CheckoutAction Action);
public sealed record VerifiedPaymentEvent(
    string ProviderEventId,
    // Null when a provider supplies only its persisted transaction reference; BillingService resolves it through the unique provider/reference index.
    Guid? OrderId,
    string ProviderTransactionId,
    long AmountMinor,
    string Currency,
    bool IsPaid,
    bool IsFinal,
    DateTimeOffset OccurredAt,
    bool IsVerificationProbe = false);
public sealed record PaymentCallbackRequest(string Method, IReadOnlyDictionary<string, string> QueryParameters, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Body);
public sealed record PaymentWebhookProcessResult(Guid OrderId, string OrderStatus, bool WasDuplicate, bool WasAlreadyFinal);

public interface IPaymentProvider
{
    string ProviderName { get; }
    string CreateProviderTransactionId(Guid orderId);
    // Some providers can safely rebuild a redirect-only checkout action from a persisted URL.
    // New checkout actions are persisted in full by BillingService; this is only a compatibility
    // fallback for orders created before action snapshots existed.
    CheckoutAction? RestoreCheckoutAction(string checkoutUrl) => null;
    Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken);
    Task<VerifiedPaymentEvent> VerifyWebhookAsync(PaymentCallbackRequest request, CancellationToken cancellationToken);
    Task<VerifiedPaymentEvent?> QueryPaymentAsync(PaymentOrderRequest request, CancellationToken cancellationToken);
}
