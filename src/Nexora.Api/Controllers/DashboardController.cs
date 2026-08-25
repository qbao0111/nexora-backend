using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/dashboard")]
public sealed class DashboardController(IPracticeService practiceService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<DashboardView>>> Get(CancellationToken cancellationToken) =>
        Ok(new ApiResponse<DashboardView>(await practiceService.GetDashboardAsync(User.GetRequiredUserId(), cancellationToken)));
}
