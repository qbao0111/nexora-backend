using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Career;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/career-goals")]
public sealed class CareerGoalsController(ICareerGoalService careerGoalService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<CareerGoalResponse>>> Create(
        CreateCareerGoalRequest request,
        CancellationToken cancellationToken)
    {
        var goal = await careerGoalService.CreateAsync(User.GetRequiredUserId(), new CreateCareerGoalCommand(
            request.TargetRole,
            request.Seniority,
            request.Industry,
            request.TargetCompany,
            request.TargetJobDescriptionId,
            request.TargetDate), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = goal.Id }, new ApiResponse<CareerGoalResponse>(Map(goal)));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<CareerGoalResponse>>>> List(CancellationToken cancellationToken)
    {
        var goals = await careerGoalService.GetManyAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<CareerGoalResponse>>(goals.Select(Map).ToArray()));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<CareerGoalResponse>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<CareerGoalResponse>(Map(await careerGoalService.GetAsync(User.GetRequiredUserId(), id, cancellationToken))));

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<ApiResponse<CareerGoalResponse>>> Update(
        Guid id,
        UpdateCareerGoalRequest request,
        CancellationToken cancellationToken)
    {
        var goal = await careerGoalService.UpdateAsync(User.GetRequiredUserId(), id, new UpdateCareerGoalCommand(
            request.TargetRoleSpecified,
            request.TargetRole,
            request.SenioritySpecified,
            request.Seniority,
            request.IndustrySpecified,
            request.Industry,
            request.TargetCompanySpecified,
            request.TargetCompany,
            request.TargetJobDescriptionIdSpecified,
            request.TargetJobDescriptionId,
            request.TargetDateSpecified,
            request.TargetDate,
            request.ActiveSpecified,
            request.Active), cancellationToken);
        return Ok(new ApiResponse<CareerGoalResponse>(Map(goal)));
    }

    private static CareerGoalResponse Map(CareerGoalView goal) =>
        new(goal.Id, goal.TargetRole, goal.Seniority, goal.Industry, goal.TargetCompany,
            goal.TargetJobDescriptionId, goal.TargetDate, goal.Active, goal.CreatedAt, goal.UpdatedAt);
}
