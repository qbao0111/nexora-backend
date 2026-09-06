using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Admin;

namespace Nexora.Api.Controllers;

[ApiController, Authorize(Policy = "Admin"), Route("api/v1/admin/users")]
public sealed class AdminUsersController(IAdminService adminService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<AdminUserPageResponse>>> List(
        [FromQuery] string? query,
        [FromQuery] string? search,
        [FromQuery] string? role,
        [FromQuery] string? planCode,
        [FromQuery] string? entitlementState,
        [FromQuery] Guid? cursor,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var searchQuery = string.IsNullOrWhiteSpace(search) ? query : search;
        var page = await adminService.GetUsersAsync(searchQuery, role, planCode, entitlementState, cursor, pageSize, cancellationToken);
        return Ok(new ApiResponse<AdminUserPageResponse>(new AdminUserPageResponse(page.LastId, page.Users.Select(Map).ToArray())));
    }

    [HttpGet("{userId:guid}")]
    public async Task<ActionResult<ApiResponse<AdminUserDetailResponse>>> Get(Guid userId, CancellationToken cancellationToken)
    {
        var user = await adminService.GetUserAsync(userId, cancellationToken);
        return Ok(new ApiResponse<AdminUserDetailResponse>(MapDetail(user)));
    }

    [HttpPut("{userId:guid}/roles")]
    public async Task<ActionResult<ApiResponse<AdminUserDetailResponse>>> UpdateRoles(
        Guid userId,
        AdminUpdateRolesRequest request,
        CancellationToken cancellationToken)
    {
        var updated = await adminService.UpdateUserRolesAsync(
            User.GetRequiredUserId(),
            userId,
            new AdminUpdateRolesCommand(request.Roles, request.Reason),
            cancellationToken);
        return Ok(new ApiResponse<AdminUserDetailResponse>(MapDetail(updated)));
    }

    [HttpPut("{userId:guid}/status")]
    public async Task<ActionResult<ApiResponse<AdminUserDetailResponse>>> UpdateStatus(
        Guid userId,
        AdminUpdateStatusRequest request,
        CancellationToken cancellationToken)
    {
        var updated = await adminService.UpdateUserStatusAsync(
            User.GetRequiredUserId(),
            userId,
            new AdminUpdateStatusCommand(request.Active, request.Reason),
            cancellationToken);
        return Ok(new ApiResponse<AdminUserDetailResponse>(MapDetail(updated)));
    }

    [HttpPost("{userId:guid}/plan-grants"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<AdminGrantResponse>>> GrantPlan(Guid userId, AdminGrantRequest request, CancellationToken cancellationToken)
    {
        var grant = await adminService.GrantPlanAsync(User.GetRequiredUserId(), userId, request.PlanPriceId, request.ReplaceCurrent, request.Reason,
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<AdminGrantResponse>(new AdminGrantResponse(grant.EntitlementId, grant.PlanCode, grant.StartsAt, grant.EndsAt)));
    }

    [HttpPost("{userId:guid}/feature-adjustments"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<AdminAdjustmentResponse>>> AdjustFeature(Guid userId, AdminAdjustmentRequest request, CancellationToken cancellationToken)
    {
        var adjustment = await adminService.AdjustFeatureAsync(User.GetRequiredUserId(), userId, request.FeatureCode, request.Quantity, request.Reason,
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return Ok(new ApiResponse<AdminAdjustmentResponse>(new AdminAdjustmentResponse(adjustment.FeatureCode, adjustment.Quantity, adjustment.Available)));
    }

    private static AdminUserSummaryResponse Map(AdminUserSummaryView user) => new(
        user.Id, user.Email, user.DisplayName, user.Roles, user.Active, user.CreatedAt, user.CurrentPlanCode, user.EntitlementStatus, user.EntitlementStartsAt, user.EntitlementEndsAt);

    private static AdminUserDetailResponse MapDetail(AdminUserDetailView user) => new(
        user.Id, user.Email, user.DisplayName, user.Roles, user.Active, user.CreatedAt,
        user.CurrentEntitlement is null ? null : new EntitlementDetailResponse(
            user.CurrentEntitlement.Id, user.CurrentEntitlement.PlanCode, user.CurrentEntitlement.StartsAt, user.CurrentEntitlement.EndsAt,
            user.CurrentEntitlement.Features.Select(f => new EntitlementFeatureResponse(f.Code, f.Name, f.Enabled, f.Limit, f.Reserved, f.Consumed, f.Adjustment, f.Available, f.Unlimited)).ToArray()),
        user.RecentOrders.Select(o => new OrderResponse(o.Id, o.PlanCode, o.AmountMinor, o.Currency, o.Status, o.CreatedAt)).ToArray());
}