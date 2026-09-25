using Nexora.Data.Identity;

namespace Nexora.Data.Billing;

public sealed class Plan
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string? Badge { get; set; }
    public bool IsHighlighted { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ICollection<PlanPrice> Prices { get; } = [];
}

public sealed class PlanPrice
{
    public Guid Id { get; set; }
    public Guid PlanId { get; set; }
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int? DurationDays { get; set; }
    public int? InterviewQuota { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Plan Plan { get; set; } = null!;
    public ICollection<PlanPriceFeature> Features { get; } = [];
}

public sealed class Order
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid PlanPriceId { get; set; }
    public string PlanCodeSnapshot { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = string.Empty;
    public int? DurationDays { get; set; }
    public int? InterviewQuota { get; set; }
    public string Status { get; set; } = string.Empty;
    public string PaymentProvider { get; set; } = string.Empty;
    public string ProviderTransactionId { get; set; } = string.Empty;
    public string CheckoutUrl { get; set; } = string.Empty;
    public string? CheckoutActionSnapshot { get; set; }
    public string FeaturesSnapshot { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public PlanPrice PlanPrice { get; set; } = null!;
    public ICollection<PaymentEvent> PaymentEvents { get; } = [];
}

public sealed class PaymentEvent
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ProviderEventId { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Order Order { get; set; } = null!;
}

public sealed class Subscription
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? OrderId { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public Order? Order { get; set; }
    public Entitlement Entitlement { get; set; } = null!;
}

public sealed class Entitlement
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid SubscriptionId { get; set; }
    public string PlanCodeSnapshot { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int? InterviewLimit { get; set; }
    public int Adjustment { get; set; }
    public int Reserved { get; set; }
    public int Consumed { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public DateTimeOffset EndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid ConcurrencyToken { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public Subscription Subscription { get; set; } = null!;
    public ICollection<UsageEvent> UsageEvents { get; } = [];
    public ICollection<EntitlementFeature> FeatureEntitlements { get; } = [];
}

public sealed class UsageEvent
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid EntitlementId { get; set; }
    public string Action { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public Entitlement Entitlement { get; set; } = null!;
}

public sealed class IdempotencyRecord
{
    public Guid Id { get; set; }
    public Guid ActorId { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string RequestFingerprint { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public string Payload { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
}
