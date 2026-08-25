using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Auth;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/me")]
public sealed class MeController(IAuthService authService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<UserResponse>>> Get(CancellationToken cancellationToken) =>
        Ok(new ApiResponse<UserResponse>(Map(await authService.GetCurrentUserAsync(User.GetRequiredUserId(), cancellationToken))));

    [HttpPatch("profile")]
    public async Task<ActionResult<ApiResponse<UserResponse>>> UpdateProfile(UpdateProfileRequest request, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<UserResponse>(Map(await authService.UpdateProfileAsync(User.GetRequiredUserId(), request.DisplayName, cancellationToken))));

    private static UserResponse Map(AuthenticatedUser user) => new(user.Id, user.Email, user.DisplayName, user.Roles);
}
