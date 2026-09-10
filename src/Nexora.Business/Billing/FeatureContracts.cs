namespace Nexora.Business.Billing;

public static class FeatureValues
{
    public const string CvAnalysis = "cv_analysis";
    public const string Interview = "interview";
    public const string Scenario = "scenario";
    public const string StarBuilder = "star_builder";
    public const string AdvancedReport = "advanced_report";
    public const string ProgressAnalytics = "progress_analytics";
    public const string InterviewQuestionLimit = "interview_question_limit";

    public const string Reserve = "reserve";
    public const string Consume = "consume";
    public const string Void = "void";
    public const string Adjustment = "adjustment";

    public static readonly string[] All = [CvAnalysis, Interview, InterviewQuestionLimit, Scenario, StarBuilder, AdvancedReport, ProgressAnalytics];
}

public sealed record FeatureDefinitionView(
    Guid Id,
    string Code,
    string Name,
    string Description,
    bool IsActive,
    int SortOrder);

public sealed record PlanFeatureView(
    string Code,
    string Name,
    bool Enabled,
    int? Limit,
    bool Unlimited);

public sealed record EntitlementFeatureView(
    string Code,
    string Name,
    bool Enabled,
    int? Limit,
    int Reserved,
    int Consumed,
    int Adjustment,
    int? Available,
    bool Unlimited);

public sealed record FeatureAccessView(
    string Code,
    bool Enabled,
    int? Limit,
    int? Reserved,
    int? Consumed,
    int? Adjustment,
    int? Available,
    bool Unlimited);

public sealed record FeatureReservation(Guid EventId, Guid EntitlementFeatureId, int? Available);

public interface IFeatureEntitlementService
{
    Task<FeatureAccessView> GetAsync(Guid userId, string featureCode, CancellationToken cancellationToken);
    Task RequireEnabledAsync(Guid userId, string featureCode, CancellationToken cancellationToken);
    Task<FeatureReservation> ReserveAsync(Guid userId, string featureCode, string sourceId, string idempotencyKey, CancellationToken cancellationToken);
    Task ConsumeAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken);
    Task VoidAsync(Guid userId, Guid reservationEventId, CancellationToken cancellationToken);
    Task AdjustAsync(Guid userId, string featureCode, int quantity, string reason, string idempotencyKey, CancellationToken cancellationToken);
}
