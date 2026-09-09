using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/scenarios")]
public sealed class ScenariosController(IScenarioService scenarioService) : ControllerBase
{
    [HttpGet("categories")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ScenarioCategoryResponse>>>> Categories(CancellationToken cancellationToken)
    {
        var categories = await scenarioService.GetCategoriesAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<ScenarioCategoryResponse>>(categories.Select(category =>
            new ScenarioCategoryResponse(category.Id, category.Slug, category.Name, category.Description)).ToArray()));
    }

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

    [HttpPost("{scenarioId:guid}/retry")]
    public async Task<ActionResult<ApiResponse<ScenarioAttemptResponse>>> Retry(Guid scenarioId, CancellationToken cancellationToken)
    {
        var attempt = await scenarioService.RetryAttemptAsync(User.GetRequiredUserId(), scenarioId,
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<ScenarioAttemptResponse>(Map(attempt)));
    }

    [HttpGet("{slugOrId}/attempts")]
    public async Task<ActionResult<ApiResponse<ScenarioAttemptHistoryResponse>>> History(string slugOrId, CancellationToken cancellationToken)
    {
        var history = await scenarioService.GetAttemptHistoryAsync(User.GetRequiredUserId(), slugOrId, cancellationToken);
        return Ok(new ApiResponse<ScenarioAttemptHistoryResponse>(new ScenarioAttemptHistoryResponse(
            history.ScenarioId,
            history.ScenarioSlug,
            history.ScenarioTitle,
            history.CategorySlug,
            history.CategoryName,
            history.Difficulty,
            history.Competency,
            history.Attempts.Select(item => new ScenarioAttemptHistoryItemResponse(
                item.Id, item.AttemptNumber, item.Status, item.Answer, item.OverallScore,
                item.PreviousScore, item.ScoreDelta, item.Improved, item.ErrorCode, item.CreatedAt, item.CompletedAt)).ToArray(),
            new ScenarioAttemptComparisonResponse(history.Comparison.CurrentScore, history.Comparison.PreviousScore,
                history.Comparison.Delta, history.Comparison.Improved),
            history.LatestScore,
            history.BestScore)));
    }

    [HttpGet("progress")]
    public async Task<ActionResult<ApiResponse<ScenarioProgressResponse>>> Progress(CancellationToken cancellationToken)
    {
        var progress = await scenarioService.GetProgressAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<ScenarioProgressResponse>(new ScenarioProgressResponse(
            progress.RecommendedDifficulty,
            progress.AttemptCount,
            progress.CompletedAttempts,
            progress.AverageScore,
            progress.LatestScore,
            progress.BestScore,
            progress.Tracks.Select(item => new ScenarioTrackProgressResponse(item.CategorySlug, item.CategoryName,
                item.AttemptCount, item.CompletedAttempts, item.AverageScore, item.LatestScore)).ToArray(),
            progress.Competencies.Select(item => new ScenarioCompetencyProgressResponse(item.Competency, item.AttemptCount,
                item.CompletedAttempts, item.AverageScore, item.BestScore, item.LatestScore)).ToArray(),
            progress.Difficulties.Select(item => new ScenarioDifficultyProgressResponse(item.Difficulty, item.AttemptCount,
                item.CompletedAttempts, item.AverageScore)).ToArray())));
    }

    private static ScenarioAttemptResponse Map(ScenarioAttemptView attempt) =>
        new(attempt.Id, attempt.ScenarioId, attempt.ScenarioTitle, attempt.Status, attempt.Answer, attempt.Evaluation,
            attempt.ErrorCode, attempt.CreatedAt, attempt.CompletedAt);
}
