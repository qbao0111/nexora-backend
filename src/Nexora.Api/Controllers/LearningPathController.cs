using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Learning;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/learning-path")]
public sealed class LearningPathController(ILearningPathService learningPathService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<LearningPathResponse>>> Get(CancellationToken cancellationToken)
    {
        var path = await learningPathService.GetAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<LearningPathResponse>(Map(path)));
    }

    [HttpPost]
    public async Task<ActionResult<ApiResponse<LearningPathResponse>>> Generate(CancellationToken cancellationToken)
    {
        var result = await learningPathService.GenerateAsync(User.GetRequiredUserId(), cancellationToken);
        var response = new ApiResponse<LearningPathResponse>(Map(result.Path));
        return result.Created ? CreatedAtAction(nameof(Get), response) : Ok(response);
    }

    [HttpPost("refresh")]
    public async Task<ActionResult<ApiResponse<LearningPathResponse>>> Refresh(CancellationToken cancellationToken)
    {
        var path = await learningPathService.RefreshAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<LearningPathResponse>(Map(path)));
    }

    [HttpPatch("activities/{activityId:guid}")]
    public async Task<ActionResult<ApiResponse<LearningPathResponse>>> UpdateActivity(
        Guid activityId,
        UpdateLearningPathActivityRequest request,
        CancellationToken cancellationToken)
    {
        var path = await learningPathService.UpdateActivityAsync(
            User.GetRequiredUserId(),
            activityId,
            new UpdateLearningPathActivityCommand(request.Status),
            cancellationToken);
        return Ok(new ApiResponse<LearningPathResponse>(Map(path)));
    }

    private static LearningPathResponse Map(LearningPathView path) =>
        new(
            path.Id,
            path.CareerGoalId,
            path.Status,
            path.CreatedAt,
            path.UpdatedAt,
            new LearningPathProgressResponse(
                path.Progress.CompletedActivityCount,
                path.Progress.TotalActivityCount,
                path.Progress.Percentage),
            path.Milestones.Select(milestone => new LearningPathMilestoneResponse(
                milestone.Id,
                milestone.Code,
                milestone.Title,
                milestone.SortOrder,
                milestone.Status,
                milestone.Activities.Select(activity => new LearningPathActivityResponse(
                    activity.Id,
                    activity.Type,
                    activity.Title,
                    activity.Description,
                    activity.CompetencyCode,
                    activity.ResourceId,
                    activity.ExternalUrl,
                    activity.Priority,
                    activity.Status,
                    activity.SortOrder,
                    activity.CompletedAt)).ToArray())).ToArray());
}
