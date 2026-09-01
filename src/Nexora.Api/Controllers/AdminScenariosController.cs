using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Admin;

namespace Nexora.Api.Controllers;

[ApiController, Authorize(Policy = "Admin"), Route("api/v1/admin")]
public sealed class AdminScenariosController(IAdminService adminService) : ControllerBase
{
    [HttpGet("scenario-categories")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<ScenarioCategoryResponse>>>> GetCategories(CancellationToken cancellationToken)
    {
        var cats = await adminService.GetAdminCategoriesAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<ScenarioCategoryResponse>>(cats.Select(c => new ScenarioCategoryResponse(c.Id, c.Slug, c.Name, c.Description)).ToArray()));
    }

    [HttpPost("scenario-categories")]
    public async Task<ActionResult<ApiResponse<ScenarioCategoryResponse>>> CreateCategory(ScenarioCategoryCreateRequest request, CancellationToken cancellationToken)
    {
        var category = await adminService.CreateCategoryAsync(User.GetRequiredUserId(), request.Slug, request.Name, request.Description, cancellationToken);
        return StatusCode(201, new ApiResponse<ScenarioCategoryResponse>(Map(category)));
    }

    [HttpPatch("scenario-categories/{id:guid}")]
    public async Task<ActionResult<ApiResponse<ScenarioCategoryResponse>>> UpdateCategory(Guid id, ScenarioCategoryUpdateRequest request, CancellationToken cancellationToken)
    {
        var category = await adminService.UpdateCategoryAsync(User.GetRequiredUserId(), id, request.Name, request.Description, request.IsActive, cancellationToken);
        return Ok(new ApiResponse<ScenarioCategoryResponse>(Map(category)));
    }

    [HttpGet("scenarios")]
    public async Task<ActionResult<ApiResponse<IReadOnlyCollection<AdminScenarioResponse>>>> GetScenarios(CancellationToken cancellationToken)
    {
        var scenarios = await adminService.GetAdminScenariosAsync(cancellationToken);
        return Ok(new ApiResponse<IReadOnlyCollection<AdminScenarioResponse>>(scenarios.Select(Map).ToArray()));
    }

    [HttpPost("scenarios")]
    public async Task<ActionResult<ApiResponse<AdminScenarioResponse>>> CreateScenario(ScenarioCreateRequest request, CancellationToken cancellationToken)
    {
        var scenario = await adminService.CreateScenarioAsync(User.GetRequiredUserId(), new ScenarioAdminWrite(
            request.Slug, request.Title, request.Summary, request.CategoryId, request.Difficulty, request.Competency, request.EstimatedMinutes, request.Content), cancellationToken);
        return StatusCode(201, new ApiResponse<AdminScenarioResponse>(Map(scenario)));
    }

    [HttpGet("scenarios/{id:guid}")]
    public async Task<ActionResult<ApiResponse<AdminScenarioResponse>>> GetScenario(Guid id, CancellationToken cancellationToken)
    {
        var scenarios = await adminService.GetAdminScenariosAsync(cancellationToken);
        var scenario = scenarios.SingleOrDefault(item => item.Id == id) ?? throw new Nexora.Business.Common.BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy scenario.", Nexora.Business.Common.BusinessErrorKind.NotFound);
        return Ok(new ApiResponse<AdminScenarioResponse>(Map(scenario)));
    }

    [HttpPatch("scenarios/{id:guid}")]
    public async Task<ActionResult<ApiResponse<AdminScenarioResponse>>> UpdateScenario(Guid id, ScenarioUpdateRequest request, CancellationToken cancellationToken)
    {
        var scenario = await adminService.UpdateScenarioAsync(User.GetRequiredUserId(), id, new ScenarioAdminWrite(
            string.Empty, request.Title, request.Summary, request.CategoryId, request.Difficulty, request.Competency, request.EstimatedMinutes, request.Content), cancellationToken);
        return Ok(new ApiResponse<AdminScenarioResponse>(Map(scenario)));
    }

    [HttpPost("scenarios/{id:guid}/publish")]
    public async Task<ActionResult<ApiResponse<AdminScenarioResponse>>> Publish(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminScenarioResponse>(Map(await adminService.SetScenarioStatusAsync(User.GetRequiredUserId(), id, "published", cancellationToken))));

    [HttpPost("scenarios/{id:guid}/archive")]
    public async Task<ActionResult<ApiResponse<AdminScenarioResponse>>> Archive(Guid id, CancellationToken cancellationToken) =>
        Ok(new ApiResponse<AdminScenarioResponse>(Map(await adminService.SetScenarioStatusAsync(User.GetRequiredUserId(), id, "archived", cancellationToken))));

    private static ScenarioCategoryResponse Map(Nexora.Business.Practice.ScenarioCategoryView category) =>
        new(category.Id, category.Slug, category.Name, category.Description);

    private static AdminScenarioResponse Map(ScenarioAdminView scenario) =>
        new(scenario.Id, scenario.Slug, scenario.Title, scenario.Summary, scenario.CategoryId, scenario.Difficulty,
            scenario.Competency, scenario.EstimatedMinutes, scenario.Content, scenario.Status, scenario.CreatedAt, scenario.PublishedAt);
}