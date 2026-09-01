using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Admin;

namespace Nexora.Api.Controllers;

[ApiController, Authorize(Policy = "Admin"), Route("api/v1/admin")]
public sealed class AdminPlansController(IAdminService adminService) : ControllerBase
{
    [HttpGet("plans")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AdminPlanResponse>>>> GetPlans(CancellationToken cancellationToken)
    {
        var plans = await adminService.GetPlansAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<AdminPlanResponse>>(Map(plans)));
    }

    [HttpPost("plans")]
    public async Task<ActionResult<ApiResponse<AdminPlanResponse>>> CreatePlan(AdminPlanCreateRequest request, CancellationToken cancellationToken)
    {
        var plan = await adminService.CreatePlanAsync(User.GetRequiredUserId(), request.Code, request.Name, request.Description, request.Badge, request.IsHighlighted, cancellationToken);
        return StatusCode(201, new ApiResponse<AdminPlanResponse>(Map(plan)));
    }

    [HttpGet("plans/{id:guid}")]
    public async Task<ActionResult<ApiResponse<AdminPlanResponse>>> GetPlan(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminPlanResponse>(Map(await adminService.GetPlanAsync(id, cancellationToken))));

    [HttpPatch("plans/{id:guid}")]
    public async Task<ActionResult<ApiResponse<AdminPlanResponse>>> UpdatePlan(Guid id, AdminPlanUpdateRequest request, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminPlanResponse>(Map(await adminService.UpdatePlanAsync(User.GetRequiredUserId(), id, request.Name, request.Description, request.Badge, request.IsHighlighted, request.IsActive, cancellationToken))));

    [HttpPost("plans/{id:guid}/prices")]
    public async Task<ActionResult<ApiResponse<AdminPlanResponse>>> AddPrice(Guid id, AdminPriceCreateRequest request, CancellationToken cancellationToken) =>
        StatusCode(201, new ApiResponse<AdminPlanResponse>(Map(await adminService.AddPlanPriceAsync(User.GetRequiredUserId(), id, request.AmountMinor, request.Currency, request.DurationDays, request.InterviewQuota, cancellationToken))));

    [HttpPatch("plan-prices/{priceId:guid}")]
    public async Task<ActionResult<ApiResponse<AdminPlanResponse>>> UpdatePrice(Guid priceId, AdminPriceUpdateRequest request, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminPlanResponse>(Map(await adminService.UpdatePlanPriceAsync(User.GetRequiredUserId(), priceId, request.AmountMinor, request.Currency, request.DurationDays, request.InterviewQuota, request.IsActive, cancellationToken))));

    [HttpPut("plan-prices/{priceId:guid}/features")]
    public async Task<ActionResult<ApiResponse<AdminPlanResponse>>> UpdateFeatures(Guid priceId, AdminFeaturesUpdateRequest request, CancellationToken cancellationToken)
    {
        var writes = request.Features.Select(f => new AdminPlanFeatureWrite(f.FeatureCode, f.Enabled, f.Limit)).ToArray();
        return Ok(new ApiResponse<AdminPlanResponse>(Map(await adminService.UpdatePlanPriceFeaturesAsync(User.GetRequiredUserId(), priceId, writes, cancellationToken))));
    }

    [HttpGet("feature-definitions")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AdminFeatureDefinitionResponse>>>> GetFeatureDefinitions(CancellationToken cancellationToken)
    {
        var defs = await adminService.GetFeatureDefinitionsAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<AdminFeatureDefinitionResponse>>(defs.Select(d => new AdminFeatureDefinitionResponse(d.Id, d.Code, d.Name, d.Description, d.IsActive, d.SortOrder)).ToArray()));
    }

    private static AdminPlanResponse Map(AdminPlanView plan) => new(
        plan.Id, plan.Code, plan.Name, plan.Description, plan.Badge, plan.IsHighlighted, plan.SortOrder, plan.IsActive, plan.CreatedAt,
        plan.Prices.Select(p => new AdminPlanPriceResponse(p.Id, p.AmountMinor, p.Currency, p.DurationDays, p.InterviewQuota, p.IsActive,
            p.Features.Select(f => new AdminPlanFeatureResponse(f.FeatureDefinitionId, f.Code, f.Name, f.Enabled, f.Limit, f.Unlimited)).ToArray())).ToArray());

    private static AdminPlanResponse[] Map(IReadOnlyCollection<AdminPlanView> plans) => plans.Select(Map).ToArray();
}