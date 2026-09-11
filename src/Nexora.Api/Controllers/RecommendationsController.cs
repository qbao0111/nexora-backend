using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Recommendations;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/recommendations")]
public sealed class RecommendationsController(INextPracticeRecommendationService recommendationService) : ControllerBase
{
    [HttpGet("next")]
    public async Task<ActionResult<ApiResponse<NextPracticeRecommendationResponse?>>> GetNext(CancellationToken cancellationToken)
    {
        var recommendation = await recommendationService.GetAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<NextPracticeRecommendationResponse?>(recommendation is null
            ? null
            : new NextPracticeRecommendationResponse(
                recommendation.Reason,
                recommendation.ActivityType,
                recommendation.ResourceId,
                recommendation.EstimatedMinutes,
                recommendation.Priority)));
    }
}
