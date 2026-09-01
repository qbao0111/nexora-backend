using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/progress")]
public sealed class ProgressController(IProgressService progressService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ProgressResponse>>> Get(CancellationToken cancellationToken)
    {
        var progress = await progressService.GetAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<ProgressResponse>(new ProgressResponse(
            progress.CompletedInterviews,
            progress.RecentInterviewScores.Select(s => new RecentInterviewScoreResponse(s.InterviewId, s.Score, s.CompletedAt)).ToArray(),
            progress.AverageInterviewScore,
            progress.StarAverages is null ? null : new ProgressStarAveragesResponse(progress.StarAverages.Situation, progress.StarAverages.Task, progress.StarAverages.Action, progress.StarAverages.Result),
            progress.CompletedScenarios,
            progress.AverageScenarioScore,
            progress.CompletedStarAttempts,
            progress.RecentActivity.Select(a => new RecentActivityResponse(a.Kind, a.ResourceId, a.At)).ToArray())));
    }
}