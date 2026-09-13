using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/resume-analyses")]
public sealed class ResumeAnalysesController(IPracticeService practiceService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ResumeAnalysisHistoryResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var history = await practiceService.GetResumeAnalysisHistoryAsync(
            User.GetRequiredUserId(), page, pageSize, cancellationToken);
        return Ok(new ApiResponse<ResumeAnalysisHistoryResponse>(new ResumeAnalysisHistoryResponse(
            history.Items.Select(item => new ResumeAnalysisHistoryItemResponse(
                item.Id,
                item.ResumeId,
                item.Mode,
                item.Status,
                item.CreatedAt,
                item.CompletedAt,
                item.Context is null ? null : new ResumeAnalysisContextResponse(
                    item.Context.Mode,
                    item.Context.Industry,
                    item.Context.TargetRole,
                    item.Context.Seniority),
                item.ErrorCode)).ToArray(),
            history.Page,
            history.PageSize,
            history.TotalCount,
            history.HasNextPage)));
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<ResumeAnalysisView>>> Create(CreateResumeAnalysisRequest request, CancellationToken cancellationToken)
    {
        var analysis = await practiceService.StartResumeAnalysisAsync(
            User.GetRequiredUserId(),
            new StartResumeAnalysisCommand(
                request.ResumeId,
                request.Mode,
                request.JobDescriptionId,
                request.Industry,
                request.TargetRole,
                request.Seniority,
                request.CareerGoalId),
            Request.Headers["Idempotency-Key"].ToString(),
            cancellationToken);
        return StatusCode(201, new ApiResponse<ResumeAnalysisView>(analysis));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<ResumeAnalysisView>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<ResumeAnalysisView>(await practiceService.GetResumeAnalysisAsync(User.GetRequiredUserId(), id, cancellationToken)));
}
