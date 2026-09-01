using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/star-attempts")]
public sealed class StarAttemptsController(IStarAttemptService starAttemptService) : ControllerBase
{
    [HttpPost, EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<StarAttemptResponse>>> Create(StarAttemptCreateRequest request, CancellationToken cancellationToken)
    {
        var attempt = await starAttemptService.CreateAsync(User.GetRequiredUserId(), request.Question, request.Answer,
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<StarAttemptResponse>(Map(attempt)));
    }

    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<StarAttemptResponse>>>> List(CancellationToken cancellationToken)
    {
        var attempts = await starAttemptService.GetManyAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<StarAttemptResponse>>(attempts.Select(Map).ToArray()));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<StarAttemptResponse>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<StarAttemptResponse>(Map(await starAttemptService.GetAsync(User.GetRequiredUserId(), id, cancellationToken))));

    private static StarAttemptResponse Map(StarAttemptView attempt) =>
        new(attempt.Id, attempt.Question, attempt.Answer, attempt.Status, attempt.Evaluation, attempt.ErrorCode, attempt.CreatedAt, attempt.CompletedAt);
}