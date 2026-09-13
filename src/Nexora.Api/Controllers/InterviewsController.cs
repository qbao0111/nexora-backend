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
    [HttpGet]
    public async Task<ActionResult<ApiResponse<InterviewHistoryResponse>>> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var history = await practiceService.GetInterviewHistoryAsync(
            User.GetRequiredUserId(), page, pageSize, cancellationToken);
        return Ok(new ApiResponse<InterviewHistoryResponse>(new InterviewHistoryResponse(
            history.Items.Select(item => new InterviewHistoryItemResponse(
                item.Id,
                item.Status,
                item.Role,
                item.Seniority,
                item.InterviewType,
                item.Difficulty,
                item.CreatedAt,
                item.UpdatedAt,
                item.CompletedAt,
                item.AnsweredQuestionCount,
                item.IssuedQuestionCount,
                item.ReportAvailable,
                item.CareerGoalId,
                item.SourceInterviewId,
                item.SourceQuestionId,
                item.PracticeReason,
                item.FocusTopic)).ToArray(),
            history.Page,
            history.PageSize,
            history.TotalCount,
            history.HasNextPage)));
    }

    [HttpPost, EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<InterviewView>>> Start(StartInterviewRequest request, CancellationToken cancellationToken)
    {
        var interview = await practiceService.StartInterviewAsync(User.GetRequiredUserId(),
            new StartInterviewCommand(request.Role, request.Seniority, request.InterviewType, request.Difficulty, request.ResumeId, request.JobDescriptionId, request.CareerGoalId),
            Request.Headers["Idempotency-Key"].ToString(), cancellationToken);
        return StatusCode(201, new ApiResponse<InterviewView>(interview));
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ApiResponse<InterviewView>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<InterviewView>(await practiceService.GetInterviewAsync(User.GetRequiredUserId(), id, cancellationToken)));

    [HttpPost("{id:guid}/practice-again"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<InterviewView>>> PracticeAgain(
        Guid id,
        PracticeAgainRequest request,
        CancellationToken cancellationToken)
    {
        var interview = await practiceService.PracticeAgainAsync(
            User.GetRequiredUserId(),
            id,
            new PracticeAgainCommand(request.QuestionId, request.Focus, request.Reason),
            Request.Headers["Idempotency-Key"].ToString(),
            cancellationToken);
        return StatusCode(201, new ApiResponse<InterviewView>(interview));
    }

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

    [HttpPost("{id:guid}/report/retry"), EnableRateLimiting(RateLimitPolicies.AiJob)]
    public async Task<ActionResult<ApiResponse<InterviewView>>> RetryReport(Guid id, CancellationToken cancellationToken) =>
        Accepted(new ApiResponse<InterviewView>(await practiceService.RetryReportAsync(
            User.GetRequiredUserId(), id, Request.Headers["Idempotency-Key"].ToString(), cancellationToken)));

    [HttpGet("{id:guid}/report")]
    public async Task<ActionResult<ApiResponse<ReportView>>> Report(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<ReportView>(await practiceService.GetReportAsync(User.GetRequiredUserId(), id, cancellationToken)));
}
