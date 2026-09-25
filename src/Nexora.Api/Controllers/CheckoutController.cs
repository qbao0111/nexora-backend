using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Billing;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/checkout-sessions")]
public sealed class CheckoutController(IBillingService billingService) : ControllerBase
{
    [HttpPost, EnableRateLimiting(RateLimitPolicies.Checkout)]
    public async Task<ActionResult<ApiResponse<CheckoutResponse>>> Create(CheckoutRequest request, CancellationToken cancellationToken)
    {
        var checkout = await billingService.CreateCheckoutAsync(
            User.GetRequiredUserId(), request.PlanPriceId, Request.Headers["Idempotency-Key"].ToString(), ClientIpAddress(), cancellationToken);
        return StatusCode(201, new ApiResponse<CheckoutResponse>(new CheckoutResponse(
            checkout.OrderId, checkout.Status, checkout.AmountMinor, checkout.Currency, checkout.Provider, Map(checkout.Checkout), checkout.ExpiresAt)));
    }

    [HttpGet("{orderId:guid}")]
    public async Task<ActionResult<ApiResponse<CheckoutStatusResponse>>> Get(Guid orderId, CancellationToken cancellationToken)
    {
        var checkout = await billingService.GetCheckoutAsync(User.GetRequiredUserId(), orderId, cancellationToken);
        return Ok(new ApiResponse<CheckoutStatusResponse>(Map(checkout)));
    }

    [HttpPost("{orderId:guid}/refresh"), EnableRateLimiting(RateLimitPolicies.Checkout)]
    public async Task<ActionResult<ApiResponse<CheckoutStatusResponse>>> Refresh(Guid orderId, CancellationToken cancellationToken)
    {
        var checkout = await billingService.RefreshCheckoutAsync(User.GetRequiredUserId(), orderId, cancellationToken);
        return Ok(new ApiResponse<CheckoutStatusResponse>(Map(checkout)));
    }

    private static CheckoutStatusResponse Map(CheckoutStatus checkout) => new(
        checkout.OrderId,
        checkout.PlanCode,
        checkout.AmountMinor,
        checkout.Currency,
        checkout.Provider,
        checkout.Status,
        Map(checkout.Checkout),
        checkout.CreatedAt,
        checkout.UpdatedAt,
        checkout.ExpiresAt);

    private static CheckoutActionResponse? Map(CheckoutAction? action) => action is null
        ? null
        : new CheckoutActionResponse(action.Method, action.Url,
            action.Fields.Select(field => new CheckoutFieldResponse(field.Name, field.Value)).ToArray());

    private string? ClientIpAddress()
    {
        var address = HttpContext.Connection.RemoteIpAddress;
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return "127.0.0.1";
        return address.ToString();
    }
}
