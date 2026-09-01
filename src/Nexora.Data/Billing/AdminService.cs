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
        ValidateScenario(write);
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

    public async Task<ScenarioAdminView> UpdateScenarioAsync(Guid adminUserId, Guid scenarioId, ScenarioAdminWrite write, CancellationToken cancellationToken)
    {
        ValidateScenario(write);
        var scenario = await dbContext.Scenarios.SingleOrDefaultAsync(item => item.Id == scenarioId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy scenario.", BusinessErrorKind.NotFound);
        if (!await dbContext.ScenarioCategories.AnyAsync(item => item.Id == write.CategoryId && item.IsActive, cancellationToken))
            throw new BusinessException("SCENARIO_CATEGORY_NOT_FOUND", "Category không hợp lệ.", BusinessErrorKind.Validation);
        scenario.Title = write.Title.Trim();
        scenario.Summary = write.Summary.Trim();
        scenario.CategoryId = write.CategoryId;
        scenario.Difficulty = write.Difficulty.Trim();
        scenario.Competency = write.Competency.Trim();
        scenario.EstimatedMinutes = write.EstimatedMinutes;
        scenario.Content = write.Content.Trim();
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

    private static void ValidateScenario(ScenarioAdminWrite write)
    {
        if (string.IsNullOrWhiteSpace(write.Slug) || write.Slug.Trim().Length > 120) throw Validation("Slug không hợp lệ.");
        if (string.IsNullOrWhiteSpace(write.Title) || write.Title.Trim().Length > 200) throw Validation("Title không hợp lệ.");
        if (string.IsNullOrWhiteSpace(write.Summary) || write.Summary.Trim().Length > 500) throw Validation("Summary không hợp lệ.");
        if (write.Difficulty is not ("easy" or "medium" or "hard")) throw Validation("Difficulty không hợp lệ.");
        if (write.Competency.Length is 0 or > 80) throw Validation("Competency không hợp lệ.");
        if (write.EstimatedMinutes is < 1 or > 600) throw Validation("EstimatedMinutes không hợp lệ.");
        if (string.IsNullOrWhiteSpace(write.Content) || write.Content.Trim().Length > 20_000) throw Validation("Content không hợp lệ.");
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

    public async Task<AdminUserPage> GetUsersAsync(string? query, string? role, string? planCode, string? entitlementState, Guid? cursor, int pageSize, CancellationToken cancellationToken)
    {
        if (pageSize is < 1 or > 100) pageSize = 20;
        var q = dbContext.Users.AsNoTracking().Include(item => item.Profile).AsQueryable();
        if (!string.IsNullOrWhiteSpace(query))
        {
            var normalized = query.Trim().ToUpperInvariant();
#pragma warning disable CA1862
            q = q.Where(item => (item.Email ?? "").Contains(normalized) || (item.Profile != null && item.Profile.DisplayName != null && item.Profile.DisplayName.ToUpperInvariant().Contains(normalized)));
#pragma warning restore CA1862
        }
        if (cursor.HasValue) q = q.Where(item => item.Id > cursor.Value);
        var users = await q.OrderBy(item => item.Id).Take(pageSize + 1).ToArrayAsync(cancellationToken);
        var result = new List<AdminUserSummaryView>();
        var now = timeProvider.GetUtcNow();
        var userIds = users.Select(item => item.Id).ToArray();
        var entitlements = await dbContext.Entitlements.AsNoTracking().Where(item => userIds.Contains(item.UserId)).ToArrayAsync(cancellationToken);
        foreach (var user in users.Take(pageSize))
        {
            var active = entitlements.Where(item => item.UserId == user.Id && item.Status == BillingValues.Active && item.StartsAt <= now && item.EndsAt > now)
                .OrderBy(item => item.EndsAt).FirstOrDefault();
            var roles = await userManager.GetRolesAsync(user);
            if (!string.IsNullOrWhiteSpace(role) && !roles.Contains(role, StringComparer.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrWhiteSpace(planCode) && (active is null || !string.Equals(active.PlanCodeSnapshot, planCode, StringComparison.OrdinalIgnoreCase))) continue;
            if (!string.IsNullOrWhiteSpace(entitlementState) && !MatchState(entitlementState, active)) continue;
            result.Add(new AdminUserSummaryView(user.Id, user.Email ?? "", user.Profile?.DisplayName, roles.ToArray(), user.LockoutEnd is null || user.LockoutEnd <= now,
                user.CreatedAt, active?.PlanCodeSnapshot, active?.Status, active?.StartsAt, active?.EndsAt));
        }
        return new AdminUserPage(users.Length > pageSize ? users.Last().Id : null, result);
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
            .OrderBy(item => item.EndsAt).FirstOrDefault();
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
        return new AdminUserDetailView(user.Id, user.Email ?? "", user.Profile?.DisplayName, roles.ToArray(),
            user.LockoutEnd is null || user.LockoutEnd <= now, user.CreatedAt, adminEntitlement,
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