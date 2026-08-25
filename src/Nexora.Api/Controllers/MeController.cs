using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Auth;
using Nexora.Business.Billing;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/me")]
public sealed class MeController(IAuthService authService, IBillingService billingService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<UserResponse>>> Get(CancellationToken cancellationToken)
    {
        var userId = User.GetRequiredUserId();
        var user = await authService.GetCurrentUserAsync(userId, cancellationToken);
        var billing = await billingService.GetSummaryAsync(userId, cancellationToken);
        return Ok(new ApiResponse<UserResponse>(Map(user, MapBilling(billing))));
    }

    [HttpPatch("profile")]
    public async Task<ActionResult<ApiResponse<UserResponse>>> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<UserResponse>(Map(await authService.UpdateProfileAsync(User.GetRequiredUserId(), request.DisplayName, cancellationToken))));

    private static UserResponse Map(AuthenticatedUser user, BillingSummaryResponse? billing = null) =>
        new(user.Id, user.Email, user.DisplayName, user.Roles, billing);

    private static BillingSummaryResponse MapBilling(BillingSummary summary) => new(
        summary.Entitlement is null ? null : new EntitlementResponse(
            summary.Entitlement.Id,
            summary.Entitlement.PlanCode,
            summary.Entitlement.StartsAt,
            summary.Entitlement.EndsAt,
            summary.Entitlement.Limit,
            summary.Entitlement.Reserved,
            summary.Entitlement.Consumed,
            summary.Entitlement.Available),
        summary.Orders.Select(order => new OrderResponse(
            order.Id, order.PlanCode, order.AmountMinor, order.Currency, order.Status, order.CreatedAt)).ToArray());
}
