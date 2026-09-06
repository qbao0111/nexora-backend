using Nexora.Business.Billing;
using Nexora.Business.Practice;

namespace Nexora.Business.Admin;

public sealed record AdminPlanView(
    Guid Id,
    string Code,
    string Name,
    string Description,
    string? Badge,
    bool IsHighlighted,
    int SortOrder,
    bool IsActive,
    DateTimeOffset CreatedAt,
    IReadOnlyCollection<AdminPlanPriceView> Prices);

public sealed record AdminPlanPriceView(
    Guid Id,
    long AmountMinor,
    string Currency,
    int? DurationDays,
    int? InterviewQuota,
    bool IsActive,
    IReadOnlyCollection<AdminPlanFeatureView> Features);

public sealed record AdminPlanFeatureView(Guid FeatureDefinitionId, string Code, string Name, bool Enabled, int? Limit, bool Unlimited);

public sealed record AdminUserSummaryView(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyCollection<string> Roles,
    bool Active,
    DateTimeOffset CreatedAt,
    string? CurrentPlanCode,
    string? EntitlementStatus,
    DateTimeOffset? EntitlementStartsAt,
    DateTimeOffset? EntitlementEndsAt);

public sealed record AdminUserDetailView(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyCollection<string> Roles,
    bool Active,
    DateTimeOffset CreatedAt,
    AdminEntitlementView? CurrentEntitlement,
    IReadOnlyCollection<OrderView> RecentOrders);

public sealed record AdminEntitlementView(
    Guid Id,
    string PlanCode,
    string Status,
    DateTimeOffset StartsAt,
    DateTimeOffset? EndsAt,
    IReadOnlyCollection<EntitlementFeatureView> Features);

public sealed record AdminUserPage(Guid? LastId, IReadOnlyCollection<AdminUserSummaryView> Users);

public sealed record AdminGrantResult(Guid EntitlementId, string PlanCode, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record AdminAdjustmentResult(string FeatureCode, int Quantity, int? Available);

public sealed record AdminRoleView(string Name);
public sealed record AdminUpdateRolesCommand(IReadOnlyCollection<string> Roles, string Reason);
public sealed record AdminUpdateStatusCommand(bool Active, string Reason);

public interface IAdminService
{
    Task<IReadOnlyCollection<AdminRoleView>> GetRolesAsync(CancellationToken cancellationToken);
    Task<AdminUserDetailView> UpdateUserRolesAsync(Guid adminUserId, Guid targetUserId, AdminUpdateRolesCommand command, CancellationToken cancellationToken);
    Task<AdminUserDetailView> UpdateUserStatusAsync(Guid adminUserId, Guid targetUserId, AdminUpdateStatusCommand command, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<AdminPlanView>> GetPlansAsync(CancellationToken cancellationToken);
    Task<AdminPlanView> CreatePlanAsync(Guid adminUserId, string code, string name, string? description, string? badge, bool isHighlighted, CancellationToken cancellationToken);
    Task<AdminPlanView> UpdatePlanAsync(Guid adminUserId, Guid planId, string name, string? description, string? badge, bool isHighlighted, bool isActive, CancellationToken cancellationToken);
    Task<AdminPlanView> GetPlanAsync(Guid planId, CancellationToken cancellationToken);
    Task<AdminPlanView> AddPlanPriceAsync(Guid adminUserId, Guid planId, long amountMinor, string currency, int? durationDays, int? interviewQuota, CancellationToken cancellationToken);
    Task<AdminPlanView> UpdatePlanPriceAsync(Guid adminUserId, Guid priceId, long amountMinor, string currency, int? durationDays, int? interviewQuota, bool isActive, CancellationToken cancellationToken);
    Task<AdminPlanView> UpdatePlanPriceFeaturesAsync(Guid adminUserId, Guid priceId, IReadOnlyCollection<AdminPlanFeatureWrite> features, CancellationToken cancellationToken);
    Task<AdminUserPage> GetUsersAsync(string? query, string? role, string? planCode, string? entitlementState, Guid? cursor, int pageSize, CancellationToken cancellationToken);
    Task<AdminUserDetailView> GetUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<AdminGrantResult> GrantPlanAsync(Guid adminUserId, Guid userId, Guid planPriceId, bool replaceCurrent, string reason, string idempotencyKey, CancellationToken cancellationToken);
    Task<AdminAdjustmentResult> AdjustFeatureAsync(Guid adminUserId, Guid userId, string featureCode, int quantity, string reason, string idempotencyKey, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<FeatureDefinitionView>> GetFeatureDefinitionsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ScenarioCategoryView>> GetAdminCategoriesAsync(CancellationToken cancellationToken);
    Task<ScenarioCategoryView> CreateCategoryAsync(Guid adminUserId, string slug, string name, string? description, CancellationToken cancellationToken);
    Task<ScenarioCategoryView> UpdateCategoryAsync(Guid adminUserId, Guid categoryId, string name, string? description, bool isActive, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ScenarioAdminView>> GetAdminScenariosAsync(CancellationToken cancellationToken);
    Task<ScenarioAdminView> CreateScenarioAsync(Guid adminUserId, ScenarioAdminWrite write, CancellationToken cancellationToken);
    Task<ScenarioAdminView> UpdateScenarioAsync(Guid adminUserId, Guid scenarioId, ScenarioAdminUpdate update, CancellationToken cancellationToken);
    Task<ScenarioAdminView> SetScenarioStatusAsync(Guid adminUserId, Guid scenarioId, string status, CancellationToken cancellationToken);
}

public sealed record ScenarioAdminView(
    Guid Id,
    string Slug,
    string Title,
    string Summary,
    Guid CategoryId,
    string Difficulty,
    string Competency,
    int EstimatedMinutes,
    string Content,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PublishedAt);

public sealed record ScenarioAdminWrite(
    string Slug,
    string Title,
    string Summary,
    Guid CategoryId,
    string Difficulty,
    string Competency,
    int EstimatedMinutes,
    string Content);

public sealed record ScenarioAdminUpdate(
    string Title,
    string Summary,
    Guid CategoryId,
    string Difficulty,
    string Competency,
    int EstimatedMinutes,
    string Content);

public sealed record AdminPlanFeatureWrite(string FeatureCode, bool Enabled, int? Limit);
