using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.ContentReports;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1")]
public sealed class ContentReportsController(IContentReportService contentReportService) : ControllerBase
{
    [HttpPost("content-reports"), Authorize, EnableRateLimiting(RateLimitPolicies.ContentReport)]
    public async Task<ActionResult<ApiResponse<ContentReportReceiptResponse>>> Submit(
        SubmitContentReportRequest request,
        CancellationToken cancellationToken)
    {
        var receipt = await contentReportService.SubmitAsync(
            User.GetRequiredUserId(),
            new SubmitContentReportCommand(request.ContentType, request.ContentId, request.ReasonCode, request.Description),
            cancellationToken);
        return Accepted(new ApiResponse<ContentReportReceiptResponse>(new ContentReportReceiptResponse(receipt.ReportId, receipt.ReceivedAt)));
    }

    [HttpGet("admin/content-reports"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<ContentReportAdminPageResponse>>> List(
        [FromQuery] string? status,
        [FromQuery] string? contentType,
        [FromQuery] string? reasonCode,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await contentReportService.GetPageAsync(
            new ContentReportQuery(status, contentType, reasonCode, from, to, page, pageSize), cancellationToken);
        return Ok(new ApiResponse<ContentReportAdminPageResponse>(new ContentReportAdminPageResponse(
            result.Items.Select(Map).ToArray(), result.Page, result.PageSize, result.TotalCount)));
    }

    [HttpGet("admin/content-reports/{id:guid}"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<ContentReportAdminDetailResponse>>> Get(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<ContentReportAdminDetailResponse>(Map(await contentReportService.GetAsync(id, cancellationToken))));

    [HttpPost("admin/content-reports/{id:guid}/review"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<ContentReportAdminDetailResponse>>> Review(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<ContentReportAdminDetailResponse>(Map(await contentReportService.ReviewAsync(
            User.GetRequiredUserId(), id, cancellationToken))));

    [HttpPost("admin/content-reports/{id:guid}/resolve"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<ContentReportAdminDetailResponse>>> Resolve(
        Guid id,
        ResolveContentReportRequest request,
        CancellationToken cancellationToken) =>
        Ok(new ApiResponse<ContentReportAdminDetailResponse>(Map(await contentReportService.ResolveAsync(
            User.GetRequiredUserId(), id, request.Outcome, request.ResolutionCode, request.ResolutionNote, cancellationToken))));

    private static ContentReportAdminItemResponse Map(ContentReportAdminItem item) => new(
        item.Id, item.ReporterUserId, item.ContentType, item.ContentId, item.ReasonCode,
        item.Status, item.CreatedAt, item.ReviewedAt, item.ModeratorUserId);

    private static ContentReportAdminDetailResponse Map(ContentReportAdminDetail detail) => new(
        Map(detail.Report), detail.Description, detail.ContentSnapshot, detail.ResolutionCode,
        detail.ResolutionNote, detail.ResolvedAt);
}
