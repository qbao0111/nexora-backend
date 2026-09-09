using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/star-stories")]
public sealed class StarStoriesController(IStarStoryService starStoryService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<StarStoryDetailResponse>>> Create(StarStoryCreateRequest request, CancellationToken cancellationToken)
    {
        var story = await starStoryService.CreateFromAttemptAsync(User.GetRequiredUserId(),
            new StarStoryCreateCommand(request.SourceAttemptId, request.Title, request.Tags),
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<StarStoryDetailResponse>(Map(story)));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<StarStoryPageResponse>>> List(
        [FromQuery] string? search,
        [FromQuery] string? tag,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await starStoryService.ListAsync(User.GetRequiredUserId(), search, tag, page, pageSize, cancellationToken);
        return Ok(new ApiResponse<StarStoryPageResponse>(new StarStoryPageResponse(result.Total,
            result.Items.Select(item => new StarStoryListItemResponse(item.Id, item.Title, item.Tags, item.LatestScore,
                item.CreatedAt, item.UpdatedAt)).ToArray())));
    }

    [HttpGet("{storyId:guid}")]
    public async Task<ActionResult<ApiResponse<StarStoryDetailResponse>>> Get(Guid storyId, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<StarStoryDetailResponse>(Map(await starStoryService.GetAsync(User.GetRequiredUserId(), storyId, cancellationToken))));

    [HttpPatch("{storyId:guid}")]
    public async Task<ActionResult<ApiResponse<StarStoryDetailResponse>>> Update(Guid storyId, StarStoryUpdateRequest request, CancellationToken cancellationToken)
    {
        var story = await starStoryService.UpdateAsync(User.GetRequiredUserId(), storyId,
            new StarStoryUpdateCommand(request.Title, request.Tags, request.Situation, request.Task, request.Action, request.Result),
            cancellationToken);
        return Ok(new ApiResponse<StarStoryDetailResponse>(Map(story)));
    }

    [HttpPost("{storyId:guid}/evaluate"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<StarStoryDetailResponse>>> Evaluate(Guid storyId, CancellationToken cancellationToken)
    {
        var story = await starStoryService.EvaluateAsync(User.GetRequiredUserId(), storyId,
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return Ok(new ApiResponse<StarStoryDetailResponse>(Map(story)));
    }

    private static StarStoryDetailResponse Map(StarStoryDetailView story) =>
        new(story.Id, story.Title, story.Tags, story.Situation, story.Task, story.Action, story.Result,
            story.LatestScore, story.LatestEvaluation, story.LatestEvaluatedAt, story.CreatedAt, story.UpdatedAt);
}
