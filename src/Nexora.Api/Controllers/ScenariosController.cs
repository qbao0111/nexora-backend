using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/scenarios")]
public sealed class ScenariosController(IScenarioService scenarioService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ScenarioPageResponse>>> List(
        [FromQuery] string? category,
        [FromQuery] string? difficulty,
        [FromQuery] string? competency,
        [FromQuery] string? search,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await scenarioService.GetScenariosAsync(category, difficulty, competency, search, page, pageSize, cancellationToken);
        return Ok(new ApiResponse<ScenarioPageResponse>(new ScenarioPageResponse(result.Total,
            result.Items.Select(s => new ScenarioCardResponse(s.Id, s.Slug, s.Title, s.Summary, s.CategorySlug, s.CategoryName, s.Difficulty, s.Competency, s.EstimatedMinutes)).ToArray())));
    }

    [HttpGet("{slugOrId}")]
    public async Task<ActionResult<ApiResponse<ScenarioDetailResponse>>> Get(string slugOrId, CancellationToken cancellationToken)
    {
        var scenario = await scenarioService.GetScenarioAsync(slugOrId, cancellationToken);
        return Ok(new ApiResponse<ScenarioDetailResponse>(new ScenarioDetailResponse(
            scenario.Id, scenario.Slug, scenario.Title, scenario.Summary, scenario.CategorySlug, scenario.CategoryName,
            scenario.Difficulty, scenario.Competency, scenario.EstimatedMinutes, scenario.Content)));
    }
}