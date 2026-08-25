using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/resume-analyses")]
public sealed class ResumeAnalysesController(IPracticeService practiceService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<ResumeAnalysisView>>> Create(CreateResumeAnalysisRequest request, CancellationToken cancellationToken)
    {
        var analysis = await practiceService.StartResumeAnalysisAsync(
            User.GetRequiredUserId(), request.ResumeId, request.JobDescriptionId, Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<ResumeAnalysisView>(analysis));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ResumeAnalysisView>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<ResumeAnalysisView>(await practiceService.GetResumeAnalysisAsync(User.GetRequiredUserId(), id, cancellationToken)));
}
