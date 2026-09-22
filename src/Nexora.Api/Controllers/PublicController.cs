using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Business.Common;
using Nexora.Business.Platform;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1/public")]
public sealed class PublicController(IPlatformStatsService platformStatsService) : ControllerBase
{
    [HttpGet("platform-stats"), AllowAnonymous]
    public async Task<ActionResult<ApiResponse<PlatformStatsResponse>>> GetPlatformStats(
        CancellationToken cancellationToken)
    {
        var stats = await platformStatsService.GetAsync(cancellationToken);
        return Ok(new ApiResponse<PlatformStatsResponse>(new PlatformStatsResponse(
            stats.UserCount,
            stats.CompletedInterviewCount,
            stats.CompletedCvAnalysisCount,
            stats.AverageRating,
            stats.RatingCount)));
    }
}
