using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Auth;
using Nexora.Business.Billing;
using Nexora.Business.Privacy;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/me")]
public sealed class MeController(IAuthService authService, IBillingService billingService, IPrivacyService privacyService) : ControllerBase
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
    public async Task<ActionResult<ApiResponse<UserProfileResponse>>> UpdateProfile(
        UpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        var profile = await authService.UpdateProfileAsync(
            User.GetRequiredUserId(),
            request.DisplayName,
            request.YearsOfExperience,
            cancellationToken);
        return Ok(new ApiResponse<UserProfileResponse>(new UserProfileResponse(
            profile.UserId,
            profile.Email,
            profile.DisplayName,
            profile.YearsOfExperience)));
    }

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        await authService.ChangePasswordAsync(User.GetRequiredUserId(), request.CurrentPassword, request.NewPassword, cancellationToken);
        return NoContent();
    }

    [HttpGet("export")]
    public async Task<ActionResult<ApiResponse<CoreDataExport>>> Export(CancellationToken cancellationToken) =>
        Ok(new ApiResponse<CoreDataExport>(await privacyService.ExportAsync(User.GetRequiredUserId(), cancellationToken)));

    [HttpPost("deletion-requests")]
    public async Task<ActionResult<ApiResponse<DeletionRequestView>>> RequestDeletion(CancellationToken cancellationToken) =>
        Accepted(new ApiResponse<DeletionRequestView>(await privacyService.RequestDeletionAsync(
            User.GetRequiredUserId(), Request.Headers["Idempotency-Key"].ToString(), cancellationToken)));

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
            summary.Entitlement.Available,
            summary.Entitlement.Features.Select(f => new EntitlementFeatureResponse(f.Code, f.Name, f.Enabled, f.Limit, f.Reserved, f.Consumed, f.Adjustment, f.Available, f.Unlimited)).ToArray()),
        summary.Orders.Select(order => new OrderResponse(
            order.Id, order.PlanCode, order.AmountMinor, order.Currency, order.Status, order.CreatedAt)).ToArray());
}
