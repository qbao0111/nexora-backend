using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Admin;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.Data.Billing;

public sealed partial class AdminService(
    NexoraDbContext dbContext,
    UserManager<ApplicationUser> userManager,
    IBillingService billingService,
    IFeatureEntitlementService featureEntitlementService,
    TimeProvider timeProvider) : IAdminService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyCollection<FeatureDefinitionView>> GetFeatureDefinitionsAsync(CancellationToken cancellationToken) =>
        await dbContext.FeatureDefinitions.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.SortOrder)
            .Select(item => new FeatureDefinitionView(item.Id, item.Code, item.Name, item.Description, item.IsActive, item.SortOrder))
            .ToArrayAsync(cancellationToken);

    public async Task<IReadOnlyCollection<ScenarioCategoryView>> GetAdminCategoriesAsync(CancellationToken cancellationToken) =>
        await dbContext.ScenarioCategories.AsNoTracking().OrderBy(item => item.SortOrder)
            .Select(item => new ScenarioCategoryView(item.Id, item.Slug, item.Name, item.Description, item.SortOrder, item.IsActive))
            .ToArrayAsync(cancellationToken);

    public async Task<ScenarioCategoryView> CreateCategoryAsync(Guid adminUserId, string slug, string name, string? description, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(slug) || slug.Trim().Length > 80) throw Validation("Slug không hợp lệ.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120) throw Validation("Name không hợp lệ.");
        if (await dbContext.ScenarioCategories.AnyAsync(item => item.Slug == slug.Trim(), cancellationToken))
            throw new BusinessException("SCENARIO_CATEGORY_EXISTS", "Slug category đã tồn tại.", BusinessErrorKind.Conflict);
        var now = timeProvider.GetUtcNow();
        var category = new ScenarioCategory
        {
            Id = Guid.NewGuid(),
            Slug = slug.Trim().ToLowerInvariant(),
            Name = name.Trim(),
            Description = description?.Trim(),
            SortOrder = await dbContext.ScenarioCategories.MaxAsync(item => (int?)item.SortOrder, cancellationToken) + 1 ?? 0,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.ScenarioCategories.Add(category);
        await AuditAsync(adminUserId, "scenario.category.create", "scenario_category", category.Id.ToString("N"), $"Created category {category.Slug}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ScenarioCategoryView(category.Id, category.Slug, category.Name, category.Description, category.SortOrder, category.IsActive);
    }

    public async Task<ScenarioCategoryView> UpdateCategoryAsync(Guid adminUserId, Guid categoryId, string name, string? description, bool isActive, CancellationToken cancellationToken)
    {
        var category = await dbContext.ScenarioCategories.SingleOrDefaultAsync(item => item.Id == categoryId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_CATEGORY_NOT_FOUND", "Không tìm thấy category.", BusinessErrorKind.NotFound);
        category.Name = name.Trim();
        category.Description = description?.Trim();
        category.IsActive = isActive;
        category.UpdatedAt = timeProvider.GetUtcNow();
        await AuditAsync(adminUserId, "scenario.category.update", "scenario_category", categoryId.ToString("N"), $"Updated category {category.Slug}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ScenarioCategoryView(category.Id, category.Slug, category.Name, category.Description, category.SortOrder, category.IsActive);
    }

    public async Task<IReadOnlyCollection<ScenarioAdminView>> GetAdminScenariosAsync(CancellationToken cancellationToken) =>
        await dbContext.Scenarios.AsNoTracking().OrderBy(item => item.SortOrder).Select(item => MapScenario(item)).ToArrayAsync(cancellationToken);

    public async Task<ScenarioAdminView> CreateScenarioAsync(Guid adminUserId, ScenarioAdminWrite write, CancellationToken cancellationToken)
    {
        ValidateScenarioCreate(write);
        if (await dbContext.Scenarios.AnyAsync(item => item.Slug == write.Slug.Trim(), cancellationToken))
            throw new BusinessException("SCENARIO_SLUG_EXISTS", "Slug scenario đã tồn tại.", BusinessErrorKind.Conflict);
        if (!await dbContext.ScenarioCategories.AnyAsync(item => item.Id == write.CategoryId && item.IsActive, cancellationToken))
            throw new BusinessException("SCENARIO_CATEGORY_NOT_FOUND", "Category không hợp lệ.", BusinessErrorKind.Validation);
        var now = timeProvider.GetUtcNow();
        var scenario = new Scenario
        {
            Id = Guid.NewGuid(),
            Slug = write.Slug.Trim().ToLowerInvariant(),
            Title = write.Title.Trim(),
            Summary = write.Summary.Trim(),
            CategoryId = write.CategoryId,
            Difficulty = write.Difficulty.Trim(),
            Competency = write.Competency.Trim(),
            EstimatedMinutes = write.EstimatedMinutes,
            Content = write.Content.Trim(),
            SortOrder = await dbContext.Scenarios.MaxAsync(item => (int?)item.SortOrder, cancellationToken) + 1 ?? 0,
            Status = PracticeFeatureValues.Draft,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.Scenarios.Add(scenario);
        await AuditAsync(adminUserId, "scenario.create", "scenario", scenario.Id.ToString("N"), $"Created scenario {scenario.Slug}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapScenario(scenario);
    }

    public async Task<ScenarioAdminView> UpdateScenarioAsync(Guid adminUserId, Guid scenarioId, ScenarioAdminUpdate update, CancellationToken cancellationToken)
    {
        ValidateScenarioUpdate(update);
        var scenario = await dbContext.Scenarios.SingleOrDefaultAsync(item => item.Id == scenarioId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy scenario.", BusinessErrorKind.NotFound);
        if (!await dbContext.ScenarioCategories.AnyAsync(item => item.Id == update.CategoryId && item.IsActive, cancellationToken))
            throw new BusinessException("SCENARIO_CATEGORY_NOT_FOUND", "Category không hợp lệ.", BusinessErrorKind.Validation);
        scenario.Title = update.Title.Trim();
        scenario.Summary = update.Summary.Trim();
        scenario.CategoryId = update.CategoryId;
        scenario.Difficulty = update.Difficulty.Trim();
        scenario.Competency = update.Competency.Trim();
        scenario.EstimatedMinutes = update.EstimatedMinutes;
        scenario.Content = update.Content.Trim();
        scenario.UpdatedAt = timeProvider.GetUtcNow();
        await AuditAsync(adminUserId, "scenario.update", "scenario", scenarioId.ToString("N"), $"Updated scenario {scenario.Slug}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapScenario(scenario);
    }

    public async Task<ScenarioAdminView> SetScenarioStatusAsync(Guid adminUserId, Guid scenarioId, string status, CancellationToken cancellationToken)
    {
        var scenario = await dbContext.Scenarios.SingleOrDefaultAsync(item => item.Id == scenarioId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy scenario.", BusinessErrorKind.NotFound);
        if (status is not (PracticeFeatureValues.Draft or PracticeFeatureValues.Published or PracticeFeatureValues.Archived))
            throw Validation("Status không hợp lệ.");
        var hasAttempts = await dbContext.ScenarioAttempts.AnyAsync(item => item.ScenarioId == scenarioId, cancellationToken);
        if (hasAttempts && status == PracticeFeatureValues.Archived) scenario.Status = PracticeFeatureValues.Archived;
        else if (status == PracticeFeatureValues.Published) scenario.Status = PracticeFeatureValues.Published;
        else if (!hasAttempts) scenario.Status = status;
        scenario.PublishedAt = scenario.Status == PracticeFeatureValues.Published ? timeProvider.GetUtcNow() : scenario.PublishedAt;
        scenario.UpdatedAt = timeProvider.GetUtcNow();
        await AuditAsync(adminUserId, $"scenario.{scenario.Status}", "scenario", scenarioId.ToString("N"), $"Scenario status to {scenario.Status}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapScenario(scenario);
    }

    private static void ValidateScenarioCreate(ScenarioAdminWrite write)
    {
        if (string.IsNullOrWhiteSpace(write.Slug) || write.Slug.Trim().Length > 120) throw Validation("Slug không hợp lệ.");
        ValidateScenarioCommon(write.Title, write.Summary, write.Difficulty, write.Competency, write.EstimatedMinutes, write.Content);
    }

    private static void ValidateScenarioUpdate(ScenarioAdminUpdate update)
    {
        ValidateScenarioCommon(update.Title, update.Summary, update.Difficulty, update.Competency, update.EstimatedMinutes, update.Content);
    }

    private static void ValidateScenarioCommon(string title, string summary, string difficulty, string competency, int estimatedMinutes, string content)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 200) throw Validation("Title không hợp lệ.");
        if (string.IsNullOrWhiteSpace(summary) || summary.Trim().Length > 500) throw Validation("Summary không hợp lệ.");
        if (difficulty is not ("easy" or "medium" or "hard")) throw Validation("Difficulty không hợp lệ.");
        if (competency.Length is 0 or > 80) throw Validation("Competency không hợp lệ.");
        if (estimatedMinutes is < 1 or > 600) throw Validation("EstimatedMinutes không hợp lệ.");
        if (string.IsNullOrWhiteSpace(content) || content.Trim().Length > 20_000) throw Validation("Content không hợp lệ.");
    }

    private static ScenarioAdminView MapScenario(Scenario scenario) =>
        new(scenario.Id, scenario.Slug, scenario.Title, scenario.Summary, scenario.CategoryId, scenario.Difficulty, scenario.Competency,
            scenario.EstimatedMinutes, scenario.Content, scenario.Status, scenario.CreatedAt, scenario.PublishedAt);

    public async Task<IReadOnlyCollection<AdminPlanView>> GetPlansAsync(CancellationToken cancellationToken) =>
        await dbContext.Plans.OrderBy(item => item.SortOrder).Select(plan => MapPlan(plan, plan.Prices.OrderBy(p => p.AmountMinor))).ToArrayAsync(cancellationToken);
    public async Task<AdminPlanView> CreatePlanAsync(Guid adminUserId, string code, string name, string? description, string? badge, bool isHighlighted, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 40) throw Validation("Plan code không hợp lệ.");
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120) throw Validation("Plan name không hợp lệ.");
        if (await dbContext.Plans.AnyAsync(item => item.Code == code.Trim(), cancellationToken))
            throw new BusinessException("PLAN_CODE_EXISTS", "Plan code đã tồn tại.", BusinessErrorKind.Conflict);
        var now = timeProvider.GetUtcNow();
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Code = code.Trim().ToLowerInvariant(),
            Name = name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            Badge = badge?.Trim(),
            IsHighlighted = isHighlighted,
            SortOrder = await dbContext.Plans.MaxAsync(item => (int?)item.SortOrder, cancellationToken) + 1 ?? 0,
            IsActive = true,
            CreatedAt = now
        };
        dbContext.Plans.Add(plan);
        await AuditAsync(adminUserId, "plan.create", "plan", plan.Id.ToString("N"), $"Created plan {plan.Code}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapPlan(plan, []);
    }

    public async Task<AdminPlanView> UpdatePlanAsync(Guid adminUserId, Guid planId, string name, string? description, string? badge, bool isHighlighted, bool isActive, CancellationToken cancellationToken)
    {
        var plan = await dbContext.Plans.Include(item => item.Prices).SingleOrDefaultAsync(item => item.Id == planId, cancellationToken)
            ?? throw new BusinessException("PLAN_NOT_FOUND", "Không tìm thấy plan.", BusinessErrorKind.NotFound);
        plan.Name = name.Trim();
        plan.Description = description?.Trim() ?? string.Empty;
        plan.Badge = badge?.Trim();
        plan.IsHighlighted = isHighlighted;
        plan.IsActive = isActive;
        await AuditAsync(adminUserId, "plan.update", "plan", planId.ToString("N"), $"Updated plan {plan.Code}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapPlan(plan, plan.Prices.OrderBy(p => p.AmountMinor));
    }

    public async Task<AdminPlanView> GetPlanAsync(Guid planId, CancellationToken cancellationToken)
    {
        var plan = await dbContext.Plans.Include(item => item.Prices).ThenInclude(item => item.Features).ThenInclude(item => item.FeatureDefinition)
            .SingleOrDefaultAsync(item => item.Id == planId, cancellationToken) ?? throw new BusinessException("PLAN_NOT_FOUND", "Không tìm thấy plan.", BusinessErrorKind.NotFound);
        return MapPlan(plan, plan.Prices.OrderBy(p => p.AmountMinor));
    }

    public async Task<AdminPlanView> AddPlanPriceAsync(Guid adminUserId, Guid planId, long amountMinor, string currency, int? durationDays, int? interviewQuota, CancellationToken cancellationToken)
    {
        var plan = await dbContext.Plans.Include(item => item.Prices).SingleOrDefaultAsync(item => item.Id == planId, cancellationToken)
            ?? throw new BusinessException("PLAN_NOT_FOUND", "Không tìm thấy plan.", BusinessErrorKind.NotFound);
        if (amountMinor < 0) throw Validation("Amount không hợp lệ.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length > 3) throw Validation("Currency không hợp lệ.");
        if (durationDays is < 0 or > 3650) throw Validation("Duration không hợp lệ.");
        if (interviewQuota is < 0 or > 100_000) throw Validation("Interview quota không hợp lệ.");

        var now = timeProvider.GetUtcNow();
        var price = new PlanPrice
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            AmountMinor = amountMinor,
            Currency = currency.Trim().ToUpperInvariant(),
            DurationDays = durationDays,
            InterviewQuota = interviewQuota,
            IsActive = true,
            CreatedAt = now
        };
        dbContext.PlanPrices.Add(price);
        plan.Prices.Add(price);
        await AuditAsync(adminUserId, "plan.price.create", "plan_price", price.Id.ToString("N"),
            $"Created price {amountMinor} {currency} for plan {plan.Code}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapPlan(plan, plan.Prices.OrderBy(p => p.AmountMinor));
    }

    public async Task<AdminPlanView> UpdatePlanPriceAsync(Guid adminUserId, Guid priceId, long amountMinor, string currency, int? durationDays, int? interviewQuota, bool isActive, CancellationToken cancellationToken)
    {
        var price = await dbContext.PlanPrices.Include(item => item.Plan).ThenInclude(item => item.Prices).SingleOrDefaultAsync(item => item.Id == priceId, cancellationToken)
            ?? throw new BusinessException("PLAN_PRICE_NOT_FOUND", "Không tìm thấy plan price.", BusinessErrorKind.NotFound);
        if (amountMinor < 0) throw Validation("Amount không hợp lệ.");
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length > 3) throw Validation("Currency không hợp lệ.");
        if (durationDays < 0) throw Validation("Duration không hợp lệ.");
        if (interviewQuota < 0) throw Validation("Interview quota không hợp lệ.");

        var hasOrders = await dbContext.Orders.AnyAsync(item => item.PlanPriceId == priceId, cancellationToken);
        if (hasOrders)
        {
            var normalizedCurrency = currency.Trim().ToUpperInvariant();
            if (price.AmountMinor != amountMinor ||
                !string.Equals(price.Currency, normalizedCurrency, StringComparison.OrdinalIgnoreCase) ||
                price.DurationDays != durationDays ||
                price.InterviewQuota != interviewQuota)
            {
                throw new BusinessException("PLAN_PRICE_COMMERCIAL_FIELDS_IMMUTABLE",
                    "Mức giá đã có đơn hàng lịch sử không được sửa đổi giá, thời hạn hoặc lượt phỏng vấn. Vui lòng tạo mức giá mới.",
                    BusinessErrorKind.Conflict);
            }
        }
        else
        {
            price.AmountMinor = amountMinor;
            price.Currency = currency.Trim().ToUpperInvariant();
            price.DurationDays = durationDays;
            price.InterviewQuota = interviewQuota;
        }

        price.IsActive = isActive;
        await AuditAsync(adminUserId, "plan.price.update", "plan_price", priceId.ToString("N"),
            $"Updated price to {price.AmountMinor} {price.Currency}, active={isActive} for plan {price.Plan.Code}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapPlan(price.Plan, price.Plan.Prices.OrderBy(p => p.AmountMinor));
    }

    public async Task<AdminPlanView> UpdatePlanPriceFeaturesAsync(Guid adminUserId, Guid priceId, IReadOnlyCollection<AdminPlanFeatureWrite> features, CancellationToken cancellationToken)
    {
        var price = await dbContext.PlanPrices.Include(item => item.Plan).ThenInclude(item => item.Prices)
            .Include(item => item.Features).ThenInclude(item => item.FeatureDefinition)
            .SingleOrDefaultAsync(item => item.Id == priceId, cancellationToken)
            ?? throw new BusinessException("PLAN_PRICE_NOT_FOUND", "Không tìm thấy plan price.", BusinessErrorKind.NotFound);
        var featureDefs = await dbContext.FeatureDefinitions.AsNoTracking().Where(item => item.IsActive).ToArrayAsync(cancellationToken);
        var featureDefMap = featureDefs.ToDictionary(item => item.Code, item => item.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var feature in features)
        {
            if (string.Equals(feature.FeatureCode, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase))
                throw new BusinessException("INTERVIEW_FEATURE_IMMUTABLE",
                    "Lượt phỏng vấn được quản lý qua InterviewQuota của mức giá, không cấu hình qua feature matrix.",
                    BusinessErrorKind.Validation);
            if (!featureDefMap.TryGetValue(feature.FeatureCode, out var fdId))
                throw new BusinessException("FEATURE_NOT_FOUND", $"Feature code {feature.FeatureCode} không hợp lệ.", BusinessErrorKind.Validation);
            if (feature.Limit < 0) throw Validation("Limit không được âm.");
            if (!feature.Enabled && feature.Limit is not null && feature.Limit > 0) throw Validation("Disabled feature không thể có limit.");
        }

        var before = price.Features.Select(item => item.FeatureDefinition.Code).ToArray();
        dbContext.PlanPriceFeatures.RemoveRange(price.Features);
        var now = timeProvider.GetUtcNow();
        foreach (var feature in features)
        {
            price.Features.Add(new PlanPriceFeature
            {
                Id = Guid.NewGuid(),
                PlanPriceId = price.Id,
                FeatureDefinitionId = featureDefMap[feature.FeatureCode],
                IsEnabled = feature.Enabled,
                Limit = feature.Limit,
                CreatedAt = now,
                UpdatedAt = now
            });
        }
        await AuditAsync(adminUserId, "plan.price.features.update", "plan_price", priceId.ToString("N", CultureInfo.InvariantCulture),
            $"Updated features: {string.Join(", ", features.Select(f => $"{f.FeatureCode}={f.Enabled}:{f.Limit?.ToString(CultureInfo.InvariantCulture) ?? "unlimited"}"))}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        // Reload with features
        var reloaded = await dbContext.PlanPrices.Include(item => item.Plan).ThenInclude(item => item.Prices)
            .Include(item => item.Features).ThenInclude(item => item.FeatureDefinition)
            .SingleAsync(item => item.Id == priceId, cancellationToken);
        return MapPlan(reloaded.Plan, reloaded.Plan.Prices.OrderBy(p => p.AmountMinor));
    }

    public Task<IReadOnlyCollection<AdminRoleView>> GetRolesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<AdminRoleView> roles =
        [
            new(Nexora.Business.Authorization.RoleNames.User),
            new(Nexora.Business.Authorization.RoleNames.Admin)
        ];
        return Task.FromResult(roles);
    }

    public async Task<AdminUserDetailView> UpdateUserRolesAsync(Guid adminUserId, Guid targetUserId, AdminUpdateRolesCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 500)
            throw Validation("Lý do cập nhật vai trò không hợp lệ.");

        var targetUser = await userManager.FindByIdAsync(targetUserId.ToString())
            ?? throw new BusinessException("USER_NOT_FOUND", "Không tìm thấy người dùng.", BusinessErrorKind.NotFound);

        // Roles payload must contain "User"
        var normalizedRequestedRoles = command.Roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (!normalizedRequestedRoles.Contains(Nexora.Business.Authorization.RoleNames.User, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException("CANNOT_REMOVE_USER_ROLE", "Không thể xóa vai trò User của tài khoản.", BusinessErrorKind.Validation);

        var validRoles = new[] { Nexora.Business.Authorization.RoleNames.User, Nexora.Business.Authorization.RoleNames.Admin };
        foreach (var role in normalizedRequestedRoles)
        {
            if (!validRoles.Contains(role, StringComparer.OrdinalIgnoreCase))
                throw new BusinessException("INVALID_ROLE", $"Vai trò '{role}' không hợp lệ.", BusinessErrorKind.Validation);
        }

        var currentRoles = await userManager.GetRolesAsync(targetUser);

        // Self-demotion guardrail: cannot remove Admin from self
        if (adminUserId == targetUserId &&
            currentRoles.Contains(Nexora.Business.Authorization.RoleNames.Admin, StringComparer.OrdinalIgnoreCase) &&
            !normalizedRequestedRoles.Contains(Nexora.Business.Authorization.RoleNames.Admin, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessException("CANNOT_REMOVE_OWN_ADMIN_ROLE", "Quản trị viên không thể tự hạ quyền Admin của chính mình.", BusinessErrorKind.Conflict);
        }

        // Last active Admin guardrail: cannot remove Admin from last active Admin
        var requestedAdmin = normalizedRequestedRoles.Contains(Nexora.Business.Authorization.RoleNames.Admin, StringComparer.OrdinalIgnoreCase);
        var currentlyAdmin = currentRoles.Contains(Nexora.Business.Authorization.RoleNames.Admin, StringComparer.OrdinalIgnoreCase);

        if (currentlyAdmin && !requestedAdmin)
        {
            var adminRoleId = Guid.Parse("50000000-0000-0000-0000-000000000002");
            var otherActiveAdminCount = await dbContext.UserRoles
                .Join(dbContext.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur, u })
                .Where(x => x.ur.RoleId == adminRoleId && x.u.Id != targetUserId && x.u.IsActive && x.u.DeletionRequestedAt == null && x.u.DeletedAt == null)
                .CountAsync(cancellationToken);

            if (otherActiveAdminCount == 0)
            {
                throw new BusinessException("LAST_ADMIN_CANNOT_BE_DEMOTED", "Không thể gỡ bỏ vai trò Admin của quản trị viên hoạt động duy nhất.", BusinessErrorKind.Conflict);
            }
        }

        // Apply role changes
        var canonicalRolesToSet = normalizedRequestedRoles.Select(r =>
            string.Equals(r, Nexora.Business.Authorization.RoleNames.Admin, StringComparison.OrdinalIgnoreCase)
                ? Nexora.Business.Authorization.RoleNames.Admin
                : Nexora.Business.Authorization.RoleNames.User).ToArray();

        var rolesToRemove = currentRoles.Except(canonicalRolesToSet, StringComparer.OrdinalIgnoreCase).ToArray();
        var rolesToAdd = canonicalRolesToSet.Except(currentRoles, StringComparer.OrdinalIgnoreCase).ToArray();

        if (rolesToRemove.Length > 0)
        {
            var removeResult = await userManager.RemoveFromRolesAsync(targetUser, rolesToRemove);
            if (!removeResult.Succeeded)
                throw new BusinessException("ROLE_UPDATE_FAILED", "Không thể xóa vai trò cũ.", BusinessErrorKind.ExternalFailure);
        }

        if (rolesToAdd.Length > 0)
        {
            var addResult = await userManager.AddToRolesAsync(targetUser, rolesToAdd);
            if (!addResult.Succeeded)
                throw new BusinessException("ROLE_UPDATE_FAILED", "Không thể thêm vai trò mới.", BusinessErrorKind.ExternalFailure);
        }

        // Revoke sessions (refresh tokens + security stamp)
        var now = timeProvider.GetUtcNow();
        await dbContext.RefreshTokens.Where(token => token.UserId == targetUserId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);
        await userManager.UpdateSecurityStampAsync(targetUser);

        // Audit
        await AuditAsync(adminUserId, "user.roles.update", "user", targetUserId.ToString("N"),
            $"Updated roles to [{string.Join(", ", canonicalRolesToSet)}]. Reason: {command.Reason.Trim()}", cancellationToken);

        return await GetUserAsync(targetUserId, cancellationToken);
    }

    public async Task<AdminUserDetailView> UpdateUserStatusAsync(Guid adminUserId, Guid targetUserId, AdminUpdateStatusCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length > 500)
            throw Validation("Lý do thay đổi trạng thái không hợp lệ.");

        var targetUser = await userManager.FindByIdAsync(targetUserId.ToString())
            ?? throw new BusinessException("USER_NOT_FOUND", "Không tìm thấy người dùng.", BusinessErrorKind.NotFound);

        // Cannot reactivate deleted or deletion-requested accounts
        if (command.Active && (targetUser.DeletionRequestedAt is not null || targetUser.DeletedAt is not null))
        {
            throw new BusinessException("CANNOT_ACTIVATE_DELETED_USER", "Không thể kích hoạt tài khoản đã xóa hoặc đang chờ xóa.", BusinessErrorKind.Conflict);
        }

        // Cannot deactivate self
        if (!command.Active && adminUserId == targetUserId)
        {
            throw new BusinessException("CANNOT_DEACTIVATE_SELF", "Quản trị viên không thể tự vô hiệu hóa tài khoản của chính mình.", BusinessErrorKind.Conflict);
        }

        // Cannot deactivate last active admin
        if (!command.Active && await userManager.IsInRoleAsync(targetUser, Nexora.Business.Authorization.RoleNames.Admin))
        {
            var adminRoleId = Guid.Parse("50000000-0000-0000-0000-000000000002");
            var otherActiveAdminCount = await dbContext.UserRoles
                .Join(dbContext.Users, ur => ur.UserId, u => u.Id, (ur, u) => new { ur, u })
                .Where(x => x.ur.RoleId == adminRoleId && x.u.Id != targetUserId && x.u.IsActive && x.u.DeletionRequestedAt == null && x.u.DeletedAt == null)
                .CountAsync(cancellationToken);

            if (otherActiveAdminCount == 0)
            {
                throw new BusinessException("LAST_ADMIN_CANNOT_BE_DEACTIVATED", "Không thể vô hiệu hóa quản trị viên hoạt động duy nhất.", BusinessErrorKind.Conflict);
            }
        }

        targetUser.IsActive = command.Active;
        targetUser.UpdatedAt = timeProvider.GetUtcNow();
        await userManager.UpdateAsync(targetUser);

        // When deactivating, revoke all active sessions
        if (!command.Active)
        {
            var now = timeProvider.GetUtcNow();
            await dbContext.RefreshTokens.Where(token => token.UserId == targetUserId && token.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);
            await userManager.UpdateSecurityStampAsync(targetUser);
        }

        var auditAction = command.Active ? "user.reactivate" : "user.deactivate";
        await AuditAsync(adminUserId, auditAction, "user", targetUserId.ToString("N"),
            $"Set active={command.Active}. Reason: {command.Reason.Trim()}", cancellationToken);

        return await GetUserAsync(targetUserId, cancellationToken);
    }

    public async Task<AdminUserPage> GetUsersAsync(string? query, string? role, string? planCode, string? entitlementState, Guid? cursor, int pageSize, CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 100) pageSize = 20;
        var now = timeProvider.GetUtcNow();
        var q = dbContext.Users.AsNoTracking().Include(item => item.Profile).AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = $"%{query.Trim()}%";
            q = q.Where(item =>
                (item.NormalizedEmail != null && EF.Functions.Like(item.NormalizedEmail, pattern)) ||
                (item.Email != null && EF.Functions.Like(item.Email, pattern)) ||
                (item.Profile != null && item.Profile.DisplayName != null && EF.Functions.Like(item.Profile.DisplayName, pattern)));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            var roleUpper = role.Trim().ToUpperInvariant();
#pragma warning disable CA1311, CA1862, CA1304
            var targetRoleId = await dbContext.Roles
                .Where(r => (r.NormalizedName != null && r.NormalizedName == roleUpper) || (r.Name != null && r.Name.ToUpper() == roleUpper))
                .Select(r => (Guid?)r.Id)
                .FirstOrDefaultAsync(cancellationToken);
#pragma warning restore CA1311, CA1862, CA1304

            if (targetRoleId.HasValue)
            {
                q = q.Where(u => dbContext.UserRoles.Any(ur => ur.UserId == u.Id && ur.RoleId == targetRoleId.Value));
            }
            else
            {
                return new AdminUserPage(null, Array.Empty<AdminUserSummaryView>());
            }
        }

        if (!string.IsNullOrWhiteSpace(planCode))
        {
            var planPattern = planCode.Trim().ToLowerInvariant();
            var candidates = await dbContext.Entitlements.AsNoTracking()
                .Where(e => e.Status == BillingValues.Active)
                .Select(e => new { e.UserId, e.PlanCodeSnapshot, e.StartsAt, e.EndsAt })
                .ToListAsync(cancellationToken);
            var planUserIds = candidates
                .Where(e => e.StartsAt <= now && e.EndsAt > now && string.Equals(e.PlanCodeSnapshot, planPattern, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.UserId)
                .Distinct()
                .ToArray();
            q = q.Where(u => planUserIds.Contains(u.Id));
        }

        if (!string.IsNullOrWhiteSpace(entitlementState))
        {
            var state = entitlementState.Trim().ToLowerInvariant();
            if (state == "active")
            {
                var activeCandidates = await dbContext.Entitlements.AsNoTracking()
                    .Where(e => e.Status == BillingValues.Active)
                    .Select(e => new { e.UserId, e.StartsAt, e.EndsAt })
                    .ToListAsync(cancellationToken);
                var activeUserIds = activeCandidates
                    .Where(e => e.StartsAt <= now && e.EndsAt > now)
                    .Select(e => e.UserId)
                    .Distinct()
                    .ToArray();
                q = q.Where(u => activeUserIds.Contains(u.Id));
            }
            else if (state == "none")
            {
                q = q.Where(u => !dbContext.Entitlements.Any(e => e.UserId == u.Id));
            }
            else if (state == "expired")
            {
                var allEntitlements = await dbContext.Entitlements.AsNoTracking()
                    .Select(e => new { e.UserId, e.Status, e.StartsAt, e.EndsAt })
                    .ToListAsync(cancellationToken);
                var userGroups = allEntitlements.GroupBy(e => e.UserId);
                var expiredUserIds = userGroups
                    .Where(g => !g.Any(e => e.Status == BillingValues.Active && e.StartsAt <= now && e.EndsAt > now))
                    .Select(g => g.Key)
                    .ToArray();
                q = q.Where(u => expiredUserIds.Contains(u.Id));
            }
        }

        if (cursor.HasValue)
        {
            q = q.Where(item => item.Id > cursor.Value);
        }

        var users = await q.OrderBy(item => item.Id).Take(pageSize + 1).ToArrayAsync(cancellationToken);
        var pageUsers = users.Take(pageSize).ToArray();
        var userIds = pageUsers.Select(item => item.Id).ToArray();

        var entitlements = await dbContext.Entitlements.AsNoTracking()
            .Where(item => userIds.Contains(item.UserId))
            .ToArrayAsync(cancellationToken);

        var result = new List<AdminUserSummaryView>();
        foreach (var user in pageUsers)
        {
            var active = entitlements.Where(item => item.UserId == user.Id && item.Status == BillingValues.Active && item.StartsAt <= now && item.EndsAt > now)
                .OrderBy(item => string.Equals(item.PlanCodeSnapshot, "free", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .ThenBy(item => item.EndsAt).FirstOrDefault();
            var roles = await userManager.GetRolesAsync(user);
            var isEffectiveActive = user.IsActive && user.DeletionRequestedAt == null && user.DeletedAt == null;

            result.Add(new AdminUserSummaryView(
                user.Id,
                user.Email ?? "",
                user.Profile?.DisplayName,
                roles.ToArray(),
                isEffectiveActive,
                user.CreatedAt,
                active?.PlanCodeSnapshot,
                active?.Status,
                active?.StartsAt,
                active?.EndsAt));
        }

        Guid? nextCursor = users.Length > pageSize ? pageUsers.Last().Id : null;
        return new AdminUserPage(nextCursor, result);
    }

    public async Task<AdminUserDetailView> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking().Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new BusinessException("USER_NOT_FOUND", "Không tìm thấy người dùng.", BusinessErrorKind.NotFound);
        var now = timeProvider.GetUtcNow();
        var entitlementCandidates = await dbContext.Entitlements.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == BillingValues.Active).ToArrayAsync(cancellationToken);
        var entitlement = entitlementCandidates.Where(item => item.StartsAt <= now && item.EndsAt > now)
            .OrderBy(item => string.Equals(item.PlanCodeSnapshot, "free", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(item => item.EndsAt).FirstOrDefault();
        var roles = await userManager.GetRolesAsync(user);
        var orders = await dbContext.Orders.AsNoTracking().Where(item => item.UserId == userId)
            .Select(item => new OrderView(item.Id, item.PlanCodeSnapshot, item.AmountMinor, item.Currency, item.Status, item.CreatedAt))
            .ToArrayAsync(cancellationToken);
        var features = Array.Empty<EntitlementFeatureView>();
        if (entitlement is not null)
        {
            var featureList = new List<EntitlementFeatureView>();
            var interview = new EntitlementFeatureView(FeatureValues.Interview, "Phỏng vấn",
                true, entitlement.InterviewLimit, entitlement.Reserved, entitlement.Consumed, entitlement.Adjustment,
                Available(entitlement.InterviewLimit, entitlement.Reserved, entitlement.Consumed, entitlement.Adjustment),
                entitlement.InterviewLimit is null);
            featureList.Add(interview);
            var generic = await dbContext.EntitlementFeatures.AsNoTracking().Include(item => item.FeatureDefinition)
                .Where(item => item.EntitlementId == entitlement.Id).ToArrayAsync(cancellationToken);
            foreach (var ef in generic.Where(item => !string.Equals(item.FeatureCode, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase)))
            {
                featureList.Add(new EntitlementFeatureView(ef.FeatureCode, ef.FeatureDefinition.Name, ef.IsEnabled, ef.Limit,
                    ef.Reserved, ef.Consumed, ef.Adjustment, Available(ef.Limit, ef.Reserved, ef.Consumed, ef.Adjustment), ef.IsEnabled && ef.Limit is null));
            }
            features = featureList.ToArray();
        }
        var adminEntitlement = entitlement is null ? null : new AdminEntitlementView(entitlement.Id, entitlement.PlanCodeSnapshot, entitlement.Status,
            entitlement.StartsAt, entitlement.EndsAt, features);
        var isEffectiveActive = user.IsActive && user.DeletionRequestedAt == null && user.DeletedAt == null;
        return new AdminUserDetailView(user.Id, user.Email ?? "", user.Profile?.DisplayName, roles.ToArray(),
            isEffectiveActive, user.CreatedAt, adminEntitlement,
            orders.OrderByDescending(item => item.CreatedAt).Take(20).ToArray());
    }

    public async Task<AdminGrantResult> GrantPlanAsync(Guid adminUserId, Guid userId, Guid planPriceId, bool replaceCurrent, string reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var existing = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == adminUserId && item.Operation == "admin.plan.grant" && item.Key == key, cancellationToken);
        if (existing is not null)
            return new AdminGrantResult(existing.ResourceId, "", DateTimeOffset.MinValue, DateTimeOffset.MinValue);

        var price = await dbContext.PlanPrices.Include(item => item.Plan).Include(item => item.Features).ThenInclude(item => item.FeatureDefinition)
            .SingleOrDefaultAsync(item => item.Id == planPriceId && item.IsActive && item.Plan.IsActive, cancellationToken)
            ?? throw new BusinessException("PLAN_PRICE_NOT_FOUND", "Gói hoặc mức giá không hợp lệ.", BusinessErrorKind.NotFound);
        var user = await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken)
            ?? throw new BusinessException("USER_NOT_FOUND", "Không tìm thấy người dùng.", BusinessErrorKind.NotFound);
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500) throw Validation("Reason required.");

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        existing = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == adminUserId && item.Operation == "admin.plan.grant" && item.Key == key, cancellationToken);
        if (existing is not null)
            return new AdminGrantResult(existing.ResourceId, "", DateTimeOffset.MinValue, DateTimeOffset.MinValue);

        var now = timeProvider.GetUtcNow();
        var activeCandidates = await dbContext.Entitlements.Where(item => item.UserId == userId && item.Status == BillingValues.Active).ToArrayAsync(cancellationToken);
        var active = activeCandidates.Where(item => item.EndsAt > now).OrderBy(item => item.EndsAt).FirstOrDefault();
        if (active is not null && !replaceCurrent)
            throw new BusinessException("ACTIVE_ENTITLEMENT_EXISTS", "Người dùng đã có gói đang hoạt động.", BusinessErrorKind.Conflict);

        if (active is not null && replaceCurrent)
        {
            active.Status = "replaced";
            active.UpdatedAt = now;
            active.EndsAt = now;
        }

        var duration = price.DurationDays ?? 3650;
        var startsAt = now;
        var endsAt = startsAt.AddDays(duration);
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrderId = null,
            Status = BillingValues.Active,
            StartsAt = startsAt,
            EndsAt = endsAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SubscriptionId = subscription.Id,
            PlanCodeSnapshot = price.Plan.Code,
            Status = BillingValues.Active,
            InterviewLimit = price.InterviewQuota,
            StartsAt = startsAt,
            EndsAt = endsAt,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        dbContext.Subscriptions.Add(subscription);
        dbContext.Entitlements.Add(entitlement);
        await SnapshotPlanFeaturesAsync(entitlement.Id, price.Features, now, cancellationToken);

        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = adminUserId,
            Operation = "admin.plan.grant",
            Key = key,
            RequestFingerprint = planPriceId.ToString("N"),
            ResourceId = entitlement.Id,
            CreatedAt = now
        });
        await AuditAsync(adminUserId, "plan.grant", "user", userId.ToString("N"),
            $"Granted plan {price.Plan.Code} {reason} (replaceCurrent={replaceCurrent})", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminGrantResult(entitlement.Id, entitlement.PlanCodeSnapshot, startsAt, endsAt);
    }

    public async Task<AdminAdjustmentResult> AdjustFeatureAsync(Guid adminUserId, Guid userId, string featureCode, int quantity, string reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var existing = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == adminUserId && item.Operation == "admin.feature.adjust" && item.Key == key, cancellationToken);
        if (existing is not null) return new AdminAdjustmentResult(featureCode, quantity, null);

        if (quantity == 0 || string.IsNullOrWhiteSpace(reason))
            throw new BusinessException("INVALID_ADJUSTMENT", "Adjustment cần số lượng khác 0 và lý do.", BusinessErrorKind.Validation);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        existing = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == adminUserId && item.Operation == "admin.feature.adjust" && item.Key == key, cancellationToken);
        if (existing is not null) return new AdminAdjustmentResult(featureCode, quantity, null);

        if (featureCode == FeatureValues.Interview)
        {
            await billingService.AdjustInterviewQuotaAsync(userId, quantity, reason, $"{key}:admin", cancellationToken);
        }
        else
        {
            await featureEntitlementService.AdjustAsync(userId, featureCode, quantity, reason, $"{key}:admin", cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var access = await featureEntitlementService.GetAsync(userId, featureCode, cancellationToken);
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = adminUserId,
            Operation = "admin.feature.adjust",
            Key = key,
            RequestFingerprint = $"{featureCode}:{quantity}",
            ResourceId = Guid.NewGuid(),
            CreatedAt = now
        });
        await AuditAsync(adminUserId, "feature.adjust", "user", userId.ToString("N"),
            $"Adjusted {featureCode} by {quantity}. Reason: {reason}", cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AdminAdjustmentResult(featureCode, quantity, access.Available);
    }

    private async Task SnapshotPlanFeaturesAsync(Guid entitlementId, ICollection<PlanPriceFeature> priceFeatures, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var pf in priceFeatures.Where(pf => !string.Equals(pf.FeatureDefinition.Code, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase)))
        {
            dbContext.EntitlementFeatures.Add(new EntitlementFeature
            {
                Id = Guid.NewGuid(),
                EntitlementId = entitlementId,
                FeatureDefinitionId = pf.FeatureDefinitionId,
                FeatureCode = pf.FeatureDefinition.Code,
                IsEnabled = pf.IsEnabled,
                Limit = pf.Limit,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid()
            });
        }
    }

    private async Task AuditAsync(Guid adminUserId, string action, string targetType, string targetId, string reason, CancellationToken cancellationToken)
    {
        dbContext.AdminAuditEvents.Add(new AdminAuditEvent
        {
            Id = Guid.NewGuid(),
            AdminUserId = adminUserId,
            Action = action,
            TargetType = targetType,
            TargetId = targetId,
            Reason = reason,
            CreatedAt = timeProvider.GetUtcNow()
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool MatchState(string? state, Entitlement? entitlement)
    {
        if (string.IsNullOrWhiteSpace(state)) return true;
        if (state == "active") return entitlement is not null;
        if (state == "expired") return entitlement is null;
        return true;
    }

    private static AdminPlanView MapPlan(Plan plan, IEnumerable<PlanPrice> prices) =>
        new(plan.Id, plan.Code, plan.Name, plan.Description ?? string.Empty, plan.Badge, plan.IsHighlighted,
            plan.SortOrder, plan.IsActive, plan.CreatedAt,
            prices.Select(price => MapPrice(price, price.Features)).ToArray());

    private static AdminPlanPriceView MapPrice(PlanPrice price, ICollection<PlanPriceFeature> features)
    {
        var list = new List<AdminPlanFeatureView>();
        var interviewDef = features.FirstOrDefault(f => string.Equals(f.FeatureDefinition.Code, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase))?.FeatureDefinition;
        var interviewId = interviewDef?.Id ?? Guid.Empty;
        list.Add(new AdminPlanFeatureView(interviewId, FeatureValues.Interview, "Phỏng vấn", true, price.InterviewQuota, price.InterviewQuota is null));
        foreach (var f in features.Where(f => !string.Equals(f.FeatureDefinition.Code, FeatureValues.Interview, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(new AdminPlanFeatureView(f.FeatureDefinitionId, f.FeatureDefinition.Code, f.FeatureDefinition.Name, f.IsEnabled, f.Limit, f.IsEnabled && f.Limit is null));
        }
        return new(price.Id, price.AmountMinor, price.Currency, price.DurationDays, price.InterviewQuota, price.IsActive, list.ToArray());
    }

    private static int? Available(int? limit, int reserved, int consumed, int adjustment) =>
        limit is null ? null : limit.Value + adjustment - reserved - consumed;

    private static string RequireKey(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
            ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
            : value.Trim();

    private static BusinessException Validation(string message) =>
        new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);

    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
}