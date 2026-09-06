using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Business.Admin;

namespace Nexora.Api.Controllers;

[ApiController, Authorize(Policy = "Admin"), Route("api/v1/admin/roles")]
public sealed class AdminRolesController(IAdminService adminService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AdminRoleResponse>>>> GetRoles(CancellationToken cancellationToken)
    {
        var roles = await adminService.GetRolesAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<AdminRoleResponse>>(roles.Select(r => new AdminRoleResponse(r.Name)).ToArray()));
    }
}
