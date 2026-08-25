using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1/uploads")]
public sealed class UploadsController(IUploadProvider uploadProvider) : ControllerBase
{
    [Authorize, HttpPost("presign"), EnableRateLimiting(RateLimitPolicies.Upload)]
    public async Task<ActionResult<ApiResponse<UploadIntent>>> Presign(PresignUploadRequest request, CancellationToken cancellationToken)
    {
        var intent = await uploadProvider.CreateIntentAsync(User.GetRequiredUserId(), request.FileName, request.ContentType, request.Size, cancellationToken);
        return Ok(new ApiResponse<UploadIntent>(intent));
    }

    [AllowAnonymous, HttpPut("{token}")]
    public async Task<IActionResult> Upload(string token, CancellationToken cancellationToken)
    {
        await uploadProvider.UploadAsync(token, Request.Body, cancellationToken);
        return NoContent();
    }
}
