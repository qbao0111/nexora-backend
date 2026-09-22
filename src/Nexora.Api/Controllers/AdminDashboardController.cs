using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Business.Admin;

namespace Nexora.Api.Controllers;

[ApiController, Authorize(Policy = "Admin"), Route("api/v1/admin")]
public sealed class AdminDashboardController(IAdminDashboardService dashboardService) : ControllerBase
{
    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<AdminDashboardView>>> GetDashboard(
        [FromQuery] string? granularity,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? currency,
        CancellationToken cancellationToken)
    {
        var dashboard = await dashboardService.GetDashboardAsync(
            new AdminDashboardQuery(granularity, from, to, currency), cancellationToken);
        return Ok(new ApiResponse<AdminDashboardView>(dashboard));
    }

    [HttpGet("transactions")]
    public async Task<ActionResult<ApiResponse<AdminTransactionPage>>> GetTransactions(
        [FromQuery] string? search,
        [FromQuery] string? status,
        [FromQuery] string? planCode,
        [FromQuery] string? currency,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] string? cursor,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var page = await dashboardService.GetTransactionsAsync(
            new AdminTransactionQuery(search, status, planCode, currency, from, to, cursor, pageSize), cancellationToken);
        return Ok(new ApiResponse<AdminTransactionPage>(page));
    }
}
