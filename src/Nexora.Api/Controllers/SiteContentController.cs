using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Site;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1")]
public sealed class SiteContentController(ISiteContentService site) : ControllerBase
{
    [HttpGet("public/site-settings"), AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SiteSettingsView>>> PublicSettings(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(new ApiResponse<SiteSettingsView>(await site.GetSettingsAsync(false, cancellationToken)));
    }

    [HttpGet("public/pages/{key}"), AllowAnonymous]
    public async Task<ActionResult<ApiResponse<SitePageView>>> PublicPage(string key, CancellationToken cancellationToken)
    {
        var page = await site.GetPageAsync(key, false, cancellationToken);
        if (page is null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(new ApiResponse<SitePageView>(page));
    }

    [HttpGet("public/site-assets/{assetId:guid}"), AllowAnonymous]
    public async Task<IActionResult> PublicAsset(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await site.OpenAssetAsync(assetId, false, cancellationToken);
        if (asset is null) return NotFound();
        Response.Headers.CacheControl = "public, max-age=300";
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(asset.Value.Content, asset.Value.ContentType);
    }

    [HttpGet("admin/site-assets/{assetId:guid}"), Authorize(Policy = "Admin")]
    public async Task<IActionResult> AdminAsset(Guid assetId, CancellationToken cancellationToken)
    {
        var asset = await site.OpenAssetAsync(assetId, true, cancellationToken);
        if (asset is null) return NotFound();
        Response.Headers.XContentTypeOptions = "nosniff";
        return File(asset.Value.Content, asset.Value.ContentType);
    }

    [HttpGet("admin/site-settings"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<SiteSettingsView>>> AdminSettings(CancellationToken cancellationToken) =>
        Ok(new ApiResponse<SiteSettingsView>(await site.GetSettingsAsync(true, cancellationToken)));

    [HttpPut("admin/site-settings"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<SiteSettingsView>>> UpdateSettings(SiteSettingsWrite request, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<SiteSettingsView>(await site.UpdateSettingsAsync(User.GetRequiredUserId(), request, cancellationToken)));

    [HttpGet("admin/site-pages/{key}"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<SitePageView>>> AdminPage(string key, CancellationToken cancellationToken)
    {
        var page = await site.GetPageAsync(key, true, cancellationToken);
        return page is null ? NotFound() : Ok(new ApiResponse<SitePageView>(page));
    }

    [HttpPut("admin/site-pages/{key}"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<SitePageView>>> UpdatePage(string key, SitePageWrite request, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<SitePageView>(await site.UpdatePageAsync(User.GetRequiredUserId(), key, request, cancellationToken)));

    [HttpPost("admin/site-pages/{key}/publish"), Authorize(Policy = "Admin")]
    public async Task<ActionResult<ApiResponse<SitePageView>>> PublishPage(string key, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<SitePageView>(await site.PublishPageAsync(User.GetRequiredUserId(), key, cancellationToken)));

    [HttpPost("admin/site-assets"), Authorize(Policy = "Admin"), RequestSizeLimit(SiteContentRules.MaximumAssetBytes + 64 * 1024)]
    public async Task<ActionResult<ApiResponse<SiteAssetView>>> UploadAsset(IFormFile file, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        var asset = await site.UploadAssetAsync(User.GetRequiredUserId(), stream, file.ContentType, cancellationToken);
        return StatusCode(201, new ApiResponse<SiteAssetView>(asset));
    }
}
