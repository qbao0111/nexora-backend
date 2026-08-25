using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/resumes")]
public sealed class ResumesController(IPracticeService practiceService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<ResumeView>>> Create(FinalizeResumeRequest request, CancellationToken cancellationToken)
    {
        var resume = await practiceService.CreateResumeAsync(User.GetRequiredUserId(), request.UploadToken, cancellationToken);
        return StatusCode(201, new ApiResponse<ResumeView>(resume));
    }
}
