using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/interviews")]
public sealed class InterviewsController(IPracticeService practiceService) : ControllerBase
{
    [HttpPost, EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<InterviewView>>> Start(StartInterviewRequest request, CancellationToken cancellationToken)
    {
        var interview = await practiceService.StartInterviewAsync(User.GetRequiredUserId(),
            new StartInterviewCommand(request.Role, request.Seniority, request.InterviewType, request.Difficulty, request.ResumeId, request.JobDescriptionId),
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<InterviewView>(interview));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<InterviewView>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<InterviewView>(await practiceService.GetInterviewAsync(User.GetRequiredUserId(), id, cancellationToken)));

    [HttpPost("{id:guid}/answers"), EnableRateLimiting(RateLimitPolicies.Answer)]
    public async Task<ActionResult<ApiResponse<AnswerResult>>> Answer(Guid id, SubmitAnswerRequest request, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AnswerResult>(await practiceService.SubmitAnswerAsync(User.GetRequiredUserId(), id, request.QuestionId,
            request.Content, request.DurationSeconds, Request.Headers["Idempotency-Key"].ToString(), cancellationToken)));

    [HttpPost("{id:guid}/continue"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<InterviewView>>> Continue(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<InterviewView>(await practiceService.ContinueInterviewAsync(
            User.GetRequiredUserId(), id, Request.Headers["Idempotency-Key"].ToString(), cancellationToken)));

    [HttpPost("{id:guid}/complete"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<InterviewView>>> Complete(Guid id, CancellationToken cancellationToken) =>
        Accepted(new ApiResponse<InterviewView>(await practiceService.CompleteInterviewAsync(
            User.GetRequiredUserId(), id, Request.Headers["Idempotency-Key"].ToString(), cancellationToken)));

    [HttpGet("{id:guid}/report")]
    public async Task<ActionResult<ApiResponse<ReportView>>> Report(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<ReportView>(await practiceService.GetReportAsync(User.GetRequiredUserId(), id, cancellationToken)));
}
