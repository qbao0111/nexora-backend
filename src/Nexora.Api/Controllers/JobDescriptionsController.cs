using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/job-descriptions")]
public sealed class JobDescriptionsController(IPracticeService practiceService) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ApiResponse<JobDescriptionView>>> Create(CreateJobDescriptionRequest request, CancellationToken cancellationToken)
    {
        var jobDescription = await practiceService.CreateJobDescriptionAsync(User.GetRequiredUserId(), request.Title, request.Content, cancellationToken);
        return StatusCode(201, new ApiResponse<JobDescriptionView>(jobDescription));
    }
}
