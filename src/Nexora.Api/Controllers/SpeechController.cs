using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;
using Nexora.Business.Speech;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/speech")]
public sealed class SpeechController(
    IPracticeService practiceService,
    ISpeechTokenProvider speechTokenProvider) : ControllerBase
{
    [HttpPost("interviews/{interviewId:guid}/token"), EnableRateLimiting(RateLimitPolicies.SpeechToken)]
    [ProducesResponseType(typeof(ApiResponse<SpeechTokenResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ApiErrorEnvelope), StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<ApiResponse<SpeechTokenResponse>>> CreateInterviewToken(
        Guid interviewId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store, private";
        Response.Headers.Pragma = "no-cache";
        Response.Headers.Expires = "0";

        await practiceService.GetInterviewAsync(User.GetRequiredUserId(), interviewId, cancellationToken);
        var token = await speechTokenProvider.GetAsync(cancellationToken);
        return Ok(new ApiResponse<SpeechTokenResponse>(new SpeechTokenResponse(
            token.Token,
            token.Region,
            token.ExpiresAt)));
    }
}

public sealed record SpeechTokenResponse(string Token, string Region, DateTimeOffset ExpiresAt);
