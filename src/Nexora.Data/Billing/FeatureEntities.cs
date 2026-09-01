using Nexora.Data.Identity;

namespace Nexora.Data.Billing;

public sealed class FeatureDefinition
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ICollection<PlanPriceFeature> PlanPriceFeatures { get; } = [];
}

public sealed class PlanPriceFeature
{
    public Guid Id { get; set; }
    public Guid PlanPriceId { get; set; }
    public Guid FeatureDefinitionId { get; set; }
    public bool IsEnabled { get; set; }
    public int? Limit { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public PlanPrice PlanPrice { get; set; } = null!;
    public FeatureDefinition FeatureDefinition { get; set; } = null!;
}

public sealed class EntitlementFeature
{
    public Guid Id { get; set; }
    public Guid EntitlementId { get; set; }
    public Guid FeatureDefinitionId { get; set; }
    public string FeatureCode { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public int? Limit { get; set; }
    public int Reserved { get; set; }
    public int Consumed { get; set; }
    public int Adjustment { get; set; }
    public Guid ConcurrencyToken { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Entitlement Entitlement { get; set; } = null!;
    public FeatureDefinition FeatureDefinition { get; set; } = null!;
    public ICollection<FeatureUsageEvent> UsageEvents { get; } = [];
}

public sealed class FeatureUsageEvent
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid EntitlementFeatureId { get; set; }
    public string FeatureCode { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public EntitlementFeature EntitlementFeature { get; set; } = null!;
}

public sealed class AdminAuditEvent
{
    public Guid Id { get; set; }
    public Guid AdminUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string TargetType { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string? SafeMetadataJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ApplicationUser AdminUser { get; set; } = null!;
}

public sealed record PlanFeatureSnapshot(string Code, bool Enabled, int? Limit);
