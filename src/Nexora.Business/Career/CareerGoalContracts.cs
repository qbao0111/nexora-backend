using Nexora.Business.Common;

namespace Nexora.Business.Career;

public static class CareerGoalRules
{
    public const int TargetRoleMaxLength = 160;
    public const int SeniorityMaxLength = 40;
    public const int IndustryMaxLength = 120;
    public const int TargetCompanyMaxLength = 160;

    private static readonly Dictionary<string, string> SeniorityAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["intern"] = "intern",
            ["entry"] = "entry",
            ["entry-level"] = "entry",
            ["junior"] = "junior",
            ["mid"] = "mid",
            ["mid-level"] = "mid",
            ["senior"] = "senior",
            ["lead"] = "lead",
            ["staff"] = "staff",
            ["principal"] = "principal",
            ["manager"] = "manager",
            ["director"] = "director",
            ["executive"] = "executive"
        };

    public static string NormalizeRequired(string? value, int maxLength, string fieldName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 || normalized.Length > maxLength)
            throw Validation($"{fieldName} không hợp lệ.");
        return normalized;
    }

    public static string? NormalizeOptional(string? value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (normalized.Length > maxLength)
            throw Validation($"{fieldName} không hợp lệ.");
        return normalized;
    }

    public static string NormalizeSeniority(string? value)
    {
        var normalized = NormalizeRequired(value, SeniorityMaxLength, "Seniority");
        if (!SeniorityAliases.TryGetValue(normalized, out var canonical))
            throw new BusinessException("INVALID_SENIORITY", "Seniority không hợp lệ.", BusinessErrorKind.Validation);
        return canonical;
    }

    private static BusinessException Validation(string message) =>
        new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);
}

public sealed record CreateCareerGoalCommand(
    string TargetRole,
    string Seniority,
    string? Industry,
    string? TargetCompany,
    Guid? TargetJobDescriptionId,
    DateOnly? TargetDate);

public sealed record UpdateCareerGoalCommand(
    bool TargetRoleSpecified,
    string? TargetRole,
    bool SenioritySpecified,
    string? Seniority,
    bool IndustrySpecified,
    string? Industry,
    bool TargetCompanySpecified,
    string? TargetCompany,
    bool TargetJobDescriptionIdSpecified,
    Guid? TargetJobDescriptionId,
    bool TargetDateSpecified,
    DateOnly? TargetDate,
    bool ActiveSpecified,
    bool? Active);

public sealed record CareerGoalView(
    Guid Id,
    string TargetRole,
    string Seniority,
    string? Industry,
    string? TargetCompany,
    Guid? TargetJobDescriptionId,
    DateOnly? TargetDate,
    bool Active,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public interface ICareerGoalService
{
    Task<CareerGoalView> CreateAsync(Guid userId, CreateCareerGoalCommand command, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<CareerGoalView>> GetManyAsync(Guid userId, CancellationToken cancellationToken);
    Task<CareerGoalView> GetAsync(Guid userId, Guid careerGoalId, CancellationToken cancellationToken);
    Task<CareerGoalView> UpdateAsync(Guid userId, Guid careerGoalId, UpdateCareerGoalCommand command, CancellationToken cancellationToken);
    Task DeleteAsync(Guid userId, Guid careerGoalId, string idempotencyKey, CancellationToken cancellationToken);
}
