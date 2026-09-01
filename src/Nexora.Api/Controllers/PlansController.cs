using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Business.Billing;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1/plans")]
public sealed class PlansController(IBillingService billingService) : ControllerBase
{
    [AllowAnonymous, HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PlanResponseV2>>>> Get(CancellationToken cancellationToken)
    {
        var plans = await billingService.GetPlansAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<PlanResponseV2>>(plans.Select(plan => new PlanResponseV2(
            plan.Id,
            plan.Code,
            plan.Name,
            plan.Description,
            plan.Badge,
            plan.IsHighlighted,
            plan.Prices.Select(price => new PlanPriceResponseV2(
                price.Id, price.AmountMinor, price.Currency, price.DurationDays, price.InterviewQuota,
                price.Features.Select(feature => new PlanFeatureResponse(feature.Code, feature.Name, feature.Enabled, feature.Limit, feature.Unlimited)).ToArray())).ToArray())).ToArray()));
    }
}
