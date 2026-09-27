using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Privacy;

namespace Nexora.Api.Controllers;

[ApiController, AllowAnonymous, Route("api/v1/account-deletion/external")]
public sealed class ExternalAccountDeletionController(IExternalAccountDeletionService externalDeletionService) : ControllerBase
{
    [HttpPost("request"), EnableRateLimiting(RateLimitPolicies.ExternalDeletionRequest)]
    public async Task<ActionResult<ApiResponse<ExternalDeletionRequestAccepted>>> RequestExternalDeletion(
        ExternalDeletionRequest request,
        CancellationToken cancellationToken)
    {
        await externalDeletionService.RequestExternalDeletionAsync(request.Email, cancellationToken);
        return Accepted(new ApiResponse<ExternalDeletionRequestAccepted>(
            new ExternalDeletionRequestAccepted("Nếu email này có tài khoản đang hoạt động, hướng dẫn xác minh sẽ được gửi đến email đó.")));
    }

    [HttpPost("confirm"), EnableRateLimiting(RateLimitPolicies.ExternalDeletionConfirm)]
    public async Task<ActionResult<ApiResponse<DeletionRequestStatusResponse>>> Confirm(
        ExternalDeletionConfirmation request,
        CancellationToken cancellationToken)
    {
        var deletion = await externalDeletionService.ConfirmExternalDeletionAsync(request.Token, cancellationToken);
        return Accepted(new ApiResponse<DeletionRequestStatusResponse>(new DeletionRequestStatusResponse(
            deletion.Id, deletion.Status, deletion.RequestedAt, deletion.CompletedAt)));
    }
}
