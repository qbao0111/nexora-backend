using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/dev")]
public sealed class DevelopmentPracticeController(IHostEnvironment environment, IPracticeService practiceService) : ControllerBase
{
    [HttpPost("resume-analysis"), Consumes("multipart/form-data"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<DevelopmentResumeAnalysisView>>> Create(
        [FromForm] DevelopmentResumeAnalysisRequest request, CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment()) return NotFound();

        await using var content = request.File!.OpenReadStream();
        var result = await practiceService.CreateDevelopmentResumeAnalysisAsync(
            User.GetRequiredUserId(), content, request.File.FileName, request.File.ContentType, request.File.Length,
            request.JobDescription, Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<DevelopmentResumeAnalysisView>(result));
    }
}
