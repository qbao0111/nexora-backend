using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Nexora.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CheckoutRequest
{
    [Required] public Guid PlanPriceId { get; init; }
}

public sealed record PlanPriceResponse(Guid Id, long AmountMinor, string Currency, int? DurationDays, int? InterviewQuota);
public sealed record PlanResponse(Guid Id, string Code, string Name, IReadOnlyCollection<PlanPriceResponse> Prices);
public sealed record CheckoutFieldResponse(string Name, string Value);
public sealed record CheckoutActionResponse(string Method, string Url, IReadOnlyList<CheckoutFieldResponse> Fields);
public sealed record CheckoutResponse(Guid OrderId, string Status, long AmountMinor, string Currency, string Provider, CheckoutActionResponse? Checkout);
public sealed record CheckoutStatusResponse(
    Guid OrderId,
    string PlanCode,
    long AmountMinor,
    string Currency,
    string Provider,
    string Status,
    CheckoutActionResponse? Checkout,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
public sealed record EntitlementResponse(
    Guid Id,
    string PlanCode,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    int? Limit,
    int Reserved,
    int Consumed,
    int? Available,
    IReadOnlyCollection<EntitlementFeatureResponse> Features);
public sealed record EntitlementFeatureResponse(string Code, string Name, bool Enabled, int? Limit, int Reserved, int Consumed, int Adjustment, int? Available, bool Unlimited);
public sealed record OrderResponse(Guid Id, string PlanCode, long AmountMinor, string Currency, string Status, DateTimeOffset CreatedAt);
public sealed record BillingSummaryResponse(EntitlementResponse? Entitlement, IReadOnlyCollection<OrderResponse> Orders);
