using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/job-descriptions")]
public sealed class JobDescriptionsController(IPracticeService practiceService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyList<JobDescriptionView>>>> List(CancellationToken cancellationToken) =>
        Ok(new ApiResponse<IReadOnlyList<JobDescriptionView>>(
            await practiceService.GetJobDescriptionsAsync(User.GetRequiredUserId(), cancellationToken)));

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<JobDescriptionView>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<JobDescriptionView>(await practiceService.GetJobDescriptionAsync(
            User.GetRequiredUserId(), id, cancellationToken)));

    [HttpPost]
    public async Task<ActionResult<ApiResponse<JobDescriptionView>>> Create(CreateJobDescriptionRequest request, CancellationToken cancellationToken)
    {
        var jobDescription = await practiceService.CreateJobDescriptionAsync(
            User.GetRequiredUserId(), request.Title, request.Content, cancellationToken,
            Request.Headers["Idempotency-Key"].ToString());
        return StatusCode(201, new ApiResponse<JobDescriptionView>(jobDescription));
    }
}
