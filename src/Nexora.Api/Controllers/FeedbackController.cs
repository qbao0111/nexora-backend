using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Common;
using Nexora.Business.Feedback;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1")]
public sealed class FeedbackController(IProductFeedbackService feedbackService) : ControllerBase
{
    [HttpGet("me/feedback"), Authorize]
    public async Task<ActionResult<ApiResponse<FeedbackResponse?>>> GetCurrent(CancellationToken cancellationToken)
    {
        var feedback = await feedbackService.GetCurrentAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<FeedbackResponse?>(feedback is null ? null : Map(feedback)));
    }

    [HttpPut("me/feedback"), Authorize]
    public async Task<ActionResult<ApiResponse<FeedbackResponse>>> Put(
        FeedbackRequest request,
        CancellationToken cancellationToken)
    {
        var feedback = await feedbackService.UpsertAsync(
            User.GetRequiredUserId(),
            new FeedbackWriteCommand(request.Rating, request.Comment, request.AllowPublicDisplay),
            cancellationToken);
        return Ok(new ApiResponse<FeedbackResponse>(Map(feedback)));
    }

    [HttpDelete("me/feedback"), Authorize]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
    {
        await feedbackService.DeleteCurrentAsync(User.GetRequiredUserId(), cancellationToken);
        return NoContent();
    }

    [HttpGet("feedback/public"), AllowAnonymous]
    public async Task<ActionResult<ApiResponse<PublicFeedbackPageResponse>>> GetPublic(
        [FromQuery] int limit = FeedbackRules.DefaultPublicLimit,
        CancellationToken cancellationToken = default)
    {
        var feedback = await feedbackService.GetPublicAsync(limit, cancellationToken);
        return Ok(new ApiResponse<PublicFeedbackPageResponse>(new PublicFeedbackPageResponse(
            feedback.AverageRating,
            feedback.RatingCount,
            feedback.Items.Select(item => new PublicFeedbackResponse(item.Id, item.DisplayName, item.Rating, item.Comment, item.PublishedAt,
                AvatarUrls.For(item.AvatarId))).ToArray())));
    }

    [HttpGet("admin/feedback"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<AdminFeedbackPageResponse>>> ListAdmin(
        [FromQuery] string? status,
        [FromQuery] string? search,
        [FromQuery] string? query,
        [FromQuery] bool? featured,
        [FromQuery] bool? consent,
        [FromQuery] int? rating,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] bool includeDeleted = false,
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var page = await feedbackService.GetAdminPageAsync(new ProductFeedbackAdminQuery(
            status,
            string.IsNullOrWhiteSpace(search) ? query : search,
            featured,
            consent,
            includeDeleted,
            cursor,
            pageSize,
            rating,
            from,
            to), cancellationToken);
        return Ok(new ApiResponse<AdminFeedbackPageResponse>(new AdminFeedbackPageResponse(
            page.Items.Select(Map).ToArray(), page.NextCursor, page.PageSize)));
    }

    [HttpGet("admin/feedback/summary"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<AdminFeedbackSummaryResponse>>> Summary(CancellationToken cancellationToken)
    {
        var summary = await feedbackService.GetSummaryAsync(cancellationToken);
        return Ok(new ApiResponse<AdminFeedbackSummaryResponse>(new AdminFeedbackSummaryResponse(
            summary.Total, summary.Pending, summary.Approved, summary.Rejected, summary.Published,
            summary.Featured, summary.AverageRating, summary.RatingDistribution)));
    }

    [HttpPost("admin/feedback/{id:guid}/approve"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<AdminFeedbackResponse>>> Approve(
        Guid id,
        AdminFeedbackActionRequest? request,
        CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminFeedbackResponse>(Map(await feedbackService.ApproveAsync(
            User.GetRequiredUserId(), id, request?.Reason, cancellationToken))));

    [HttpPost("admin/feedback/{id:guid}/reject"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<AdminFeedbackResponse>>> Reject(
        Guid id,
        AdminFeedbackActionRequest? request,
        CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminFeedbackResponse>(Map(await feedbackService.RejectAsync(
            User.GetRequiredUserId(), id, request?.Reason, cancellationToken))));

    [HttpPost("admin/feedback/{id:guid}/feature"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<AdminFeedbackResponse>>> Feature(
        Guid id,
        AdminFeedbackActionRequest? request,
        CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminFeedbackResponse>(Map(await feedbackService.FeatureAsync(
            User.GetRequiredUserId(), id, request?.Reason, cancellationToken))));

    [HttpPost("admin/feedback/{id:guid}/unfeature"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<AdminFeedbackResponse>>> Unfeature(
        Guid id,
        AdminFeedbackActionRequest? request,
        CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminFeedbackResponse>(Map(await feedbackService.UnfeatureAsync(
            User.GetRequiredUserId(), id, request?.Reason, cancellationToken))));

    private static FeedbackResponse Map(ProductFeedbackView feedback) => new(
        feedback.Id,
        feedback.Rating,
        feedback.Comment,
        feedback.Consent,
        feedback.Status,
        feedback.Featured,
        feedback.CreatedAt,
        feedback.UpdatedAt,
        feedback.ModeratedAt,
        feedback.PublishedAt,
        feedback.DeletedAt);

    private static AdminFeedbackResponse Map(ProductFeedbackAdminView feedback) => new(
        feedback.Id,
        feedback.UserId,
        feedback.Email,
        feedback.DisplayName,
        feedback.Rating,
        feedback.Comment,
        feedback.Consent,
        feedback.Status,
        feedback.Featured,
        feedback.CreatedAt,
        feedback.UpdatedAt,
        feedback.ModeratedAt,
        feedback.PublishedAt,
        feedback.DeletedAt,
        feedback.ModeratedByUserId);
}
