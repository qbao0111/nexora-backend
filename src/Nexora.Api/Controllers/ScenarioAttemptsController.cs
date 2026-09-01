using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/scenario-attempts")]
public sealed class ScenarioAttemptsController(IScenarioService scenarioService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<ScenarioAttemptResponse>>> Create(ScenarioAttemptCreateRequest request, CancellationToken cancellationToken)
    {
        var attempt = await scenarioService.CreateAttemptAsync(User.GetRequiredUserId(), request.ScenarioId,
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<ScenarioAttemptResponse>(Map(attempt)));
    }

    [HttpPost("{id:guid}/submit"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<ScenarioAttemptResponse>>> Submit(Guid id, ScenarioAttemptSubmitRequest request, CancellationToken cancellationToken)
    {
        var attempt = await scenarioService.SubmitAttemptAsync(User.GetRequiredUserId(), id, request.Answer,
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return Accepted(new ApiResponse<ScenarioAttemptResponse>(Map(attempt)));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ScenarioAttemptResponse>>>> List(CancellationToken cancellationToken)
    {
        var attempts = await scenarioService.GetAttemptsAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<ScenarioAttemptResponse>>(attempts.Select(Map).ToArray()));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ScenarioAttemptResponse>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<ScenarioAttemptResponse>(Map(await scenarioService.GetAttemptAsync(User.GetRequiredUserId(), id, cancellationToken))));

    private static ScenarioAttemptResponse Map(ScenarioAttemptView attempt) =>
        new(attempt.Id, attempt.ScenarioId, attempt.ScenarioTitle, attempt.Status, attempt.Answer, attempt.Evaluation, attempt.ErrorCode, attempt.CreatedAt, attempt.CompletedAt);
}