using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Ai;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Speech;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/speech")]
public sealed class SpeechController(
    IPracticeService practiceService,
    ISpeechTokenProvider speechTokenProvider,
    IProviderAdmission admission,
    IConfiguration configuration) : ControllerBase
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

        var userId = User.GetRequiredUserId();
        var interview = await practiceService.GetInterviewAsync(userId, interviewId, cancellationToken);
        var sessionMinutes = configuration.GetValue("Speech:MaximumSessionMinutes", 120);
        if (interview.Status is not ("starting" or "active") ||
            DateTimeOffset.UtcNow - interview.CreatedAt > TimeSpan.FromMinutes(sessionMinutes))
            throw new BusinessException("SPEECH_SESSION_INACTIVE", "Phiên phỏng vấn không còn hỗ trợ giọng nói.", BusinessErrorKind.Conflict);
        var reservation = await admission.ReserveAsync(new ProviderAdmissionRequest(
            userId, Guid.NewGuid(), "speech.token", interviewId.ToString("N"), 1, 1), cancellationToken);
        SpeechAuthorizationToken token;
        try { token = await speechTokenProvider.GetAsync(cancellationToken); }
        finally
        {
            using var completionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await admission.CompleteAsync(reservation, null, completionTimeout.Token);
        }
        return Ok(new ApiResponse<SpeechTokenResponse>(new SpeechTokenResponse(
            token.Token,
            token.Region,
            token.ExpiresAt)));
    }
}

public sealed record SpeechTokenResponse(string Token, string Region, DateTimeOffset ExpiresAt);
