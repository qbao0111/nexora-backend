using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Auth;
using Nexora.Business.Common;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1")]
public sealed class AvatarController(IAvatarService avatarService) : ControllerBase
{
    [HttpPut("me/avatar"), Authorize, Consumes("multipart/form-data")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<ActionResult<ApiResponse<AvatarResponse>>> Upload(
        [FromForm] IFormFile? file,
        CancellationToken cancellationToken)
    {
        if (file is null)
            throw new BusinessException("AVATAR_FILE_INVALID", "Vui lòng chọn ảnh đại diện.", BusinessErrorKind.Validation);
        await using var content = file.OpenReadStream();
        var avatarId = await avatarService.UploadAsync(
            User.GetRequiredUserId(), content, file.Length, file.ContentType, cancellationToken);
        return Ok(new ApiResponse<AvatarResponse>(new AvatarResponse(AvatarUrls.For(avatarId)!)));
    }

    [HttpDelete("me/avatar"), Authorize]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken)
    {
        await avatarService.DeleteAsync(User.GetRequiredUserId(), cancellationToken);
        return NoContent();
    }

    [HttpGet("avatars/{avatarId:guid}"), AllowAnonymous]
    public async Task<IActionResult> Get(Guid avatarId, CancellationToken cancellationToken)
    {
        var image = await avatarService.OpenAsync(avatarId, cancellationToken);
        if (image is null) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers.CacheControl = "public, max-age=300";
        return File(image.Content, image.ContentType);
    }
}
