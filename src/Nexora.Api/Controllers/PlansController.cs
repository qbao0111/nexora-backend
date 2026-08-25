using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Business.Billing;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1/plans")]
public sealed class PlansController(IBillingService billingService) : ControllerBase
{
    [AllowAnonymous, HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<PlanResponse>>>> Get(CancellationToken cancellationToken)
    {
        var plans = await billingService.GetPlansAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<PlanResponse>>(plans.Select(plan => new PlanResponse(
            plan.Id,
            plan.Code,
            plan.Name,
            plan.Prices.Select(price => new PlanPriceResponse(
                price.Id, price.AmountMinor, price.Currency, price.DurationDays, price.InterviewQuota)).ToArray())).ToArray()));
    }
}
