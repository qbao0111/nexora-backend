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
            User.GetRequiredUserId(), request.PlanPriceId, Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<CheckoutResponse>(new CheckoutResponse(
            checkout.OrderId, checkout.Status, checkout.AmountMinor, checkout.Currency, checkout.Provider, checkout.CheckoutUrl)));
    }
}
