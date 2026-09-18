using System.Security.Cryptography;
using System.Text;
using Nexora.Business.Skills;

namespace Nexora.Business.Learning;

public static class LearningPathValues
{
    public const string Active = "active";
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Obsolete = "obsolete";

    public const string Scenario = "scenario";
    public const string StarDrill = "star_drill";
    public const string Interview = "interview";
    public const string ResumeImprovement = "resume_improvement";
    public const string ExternalLearning = "external_learning";

    public const string CriticalMilestone = "critical_gaps";
    public const string DevelopingMilestone = "developing_skills";
    public const string SupportingMilestone = "supporting_improvements";
}

public static class LearningPathRules
{
    public const int NumericGapThreshold = 75;
    public const int CriticalGapThreshold = 60;
    // These limits apply only to the newly generated plan, never persisted history.
    public const int MaximumCurrentActivities = 10;
    public const int MaximumQualitativeActivities = 4;
    public const int ActivityKeyMaxLength = 160;
    public const int ActivityTitleMaxLength = 200;
    public const int ActivityDescriptionMaxLength = 500;
    public const int CompetencyCodeMaxLength = 120;
    public const int ExternalUrlMaxLength = 2_048;

    public static int PriorityForScore(int score) => score < CriticalGapThreshold ? 1 : 2;

    public static string MilestoneCodeForPriority(int priority) => priority switch
    {
        1 => LearningPathValues.CriticalMilestone,
        2 => LearningPathValues.DevelopingMilestone,
        _ => LearningPathValues.SupportingMilestone
    };

    public static string MilestoneTitle(string code) => code switch
    {
        LearningPathValues.CriticalMilestone => "Fix critical gaps",
        LearningPathValues.DevelopingMilestone => "Develop emerging skills",
        LearningPathValues.SupportingMilestone => "Strengthen supporting evidence",
        _ => "Supporting improvements"
    };

    public static string Truncate(string value, int maxLength)
    {
        var normalized = value.Trim();
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd();
    }

    public static string QualitativeActivityKey(string label)
    {
        var normalized = label.Trim().ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return $"{LearningPathValues.ResumeImprovement}:qualitative:{hash[..24]}";
    }

    public static string LearningCycleActivityKey(string baseKey, DateTimeOffset evidenceAt)
    {
        const string cycleSeparator = ":cycle:";
        var material = $"{baseKey}\u001f{evidenceAt.UtcDateTime.Ticks}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant()[..24];
        var prefix = Truncate(baseKey, ActivityKeyMaxLength - cycleSeparator.Length - hash.Length);
        return $"{prefix}{cycleSeparator}{hash}";
    }
}

public sealed record LearningPathScenarioResource(Guid Id, string Competency);

public sealed record LearningPathActivityPlan(
    string Key,
    string Type,
    string Title,
    string Description,
    string? CompetencyCode,
    Guid? ResourceId,
    string? ExternalUrl,
    int Priority,
    string MilestoneCode,
    int SortOrder)
{
    public DateTimeOffset? LatestEvidenceAt { get; init; }
}

public sealed record LearningPathMilestonePlan(string Code, string Title, int SortOrder);

public sealed record LearningPathPlan(
    IReadOnlyCollection<LearningPathMilestonePlan> Milestones,
    IReadOnlyCollection<LearningPathActivityPlan> Activities);

public sealed record LearningPathProgressView(int CompletedActivityCount, int TotalActivityCount, int Percentage);

public sealed record LearningPathActivityView(
    Guid Id,
    string Type,
    string Title,
    string Description,
    string? CompetencyCode,
    Guid? ResourceId,
    string? ExternalUrl,
    int Priority,
    string Status,
    int SortOrder,
    DateTimeOffset? CompletedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record LearningPathMilestoneView(
    Guid Id,
    string Code,
    string Title,
    int SortOrder,
    string Status,
    IReadOnlyCollection<LearningPathActivityView> Activities);

public sealed record LearningPathView(
    Guid Id,
    Guid CareerGoalId,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    LearningPathProgressView Progress,
    IReadOnlyCollection<LearningPathMilestoneView> Milestones);

public sealed record LearningPathProvisionResult(LearningPathView Path, bool Created);

public sealed record UpdateLearningPathActivityCommand(string? Status);

public interface ILearningPathService
{
    Task<LearningPathView> GetAsync(Guid userId, CancellationToken cancellationToken);
    Task<LearningPathProvisionResult> GenerateAsync(Guid userId, CancellationToken cancellationToken);
    Task<LearningPathView> RefreshAsync(Guid userId, CancellationToken cancellationToken);
    Task<LearningPathView> UpdateActivityAsync(
        Guid userId,
        Guid activityId,
        UpdateLearningPathActivityCommand command,
        CancellationToken cancellationToken);
}

public static class LearningPathPlanner
{
    public static LearningPathPlan Create(
        SkillProfileView profile,
        IReadOnlyCollection<LearningPathScenarioResource> scenarioResources)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(scenarioResources);

        var resourcesByCompetency = scenarioResources
            .Where(item => item.Id != Guid.Empty && !string.IsNullOrWhiteSpace(item.Competency))
            .Select(item => new
            {
                Resource = item,
                Code = SkillProfileTaxonomy.CreateCode("scenario", item.Competency)
            })
            .Where(item => item.Code is not null)
            .GroupBy(item => item.Code!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(item => item.Resource).OrderBy(item => item.Id).First(),
                StringComparer.Ordinal);

        var activities = new List<LearningPathActivityPlan>();
        foreach (var competency in profile.Competencies
                     .Where(item => item is not null && item.Score >= 0 && item.Score < LearningPathRules.NumericGapThreshold)
                     .OrderBy(item => item.Score)
                     .ThenBy(item => item.Code, StringComparer.Ordinal))
        {
            var code = NormalizeCompetencyCode(competency.Code);
            if (code is null) continue;

            var separator = code.IndexOf('.', StringComparison.Ordinal);
            var category = code[..separator];
            var priority = LearningPathRules.PriorityForScore(competency.Score);
            var type = category switch
            {
                "scenario" when resourcesByCompetency.TryGetValue(code, out _) => LearningPathValues.Scenario,
                "scenario" => LearningPathValues.ExternalLearning,
                "behavioral" => LearningPathValues.StarDrill,
                "interview" => LearningPathValues.Interview,
                "resume" => LearningPathValues.ResumeImprovement,
                _ => null
            };
            if (type is null) continue;

            resourcesByCompetency.TryGetValue(code, out var resource);
            var name = LearningPathRules.Truncate(
                string.IsNullOrWhiteSpace(competency.Name)
                    ? SkillProfileTaxonomy.DisplayName(code[(separator + 1)..])
                    : competency.Name,
                120);
            var title = type switch
            {
                LearningPathValues.Scenario => $"Practice {name}",
                LearningPathValues.StarDrill => $"Drill {name} with STAR",
                LearningPathValues.Interview => $"Practice {name} in an interview",
                LearningPathValues.ExternalLearning => $"Study {name} with guided practice",
                _ => $"Improve {name} in your CV"
            };
            var description = type == LearningPathValues.ExternalLearning
                ? $"Practice or study the {name} competency using a suitable learning resource."
                : $"Use a focused practice session to improve {name}.";
            var key = $"{type}:{code}{(resource is null ? string.Empty : $":{resource.Id:N}")}";
            activities.Add(new LearningPathActivityPlan(
                key,
                type,
                LearningPathRules.Truncate(title, LearningPathRules.ActivityTitleMaxLength),
                LearningPathRules.Truncate(description, LearningPathRules.ActivityDescriptionMaxLength),
                code,
                resource?.Id,
                null,
                priority,
                LearningPathRules.MilestoneCodeForPriority(priority),
                0)
            {
                LatestEvidenceAt = competency.LatestEvidenceAt
            });
        }

        var qualitativeActivities = new List<LearningPathActivityPlan>();
        foreach (var signal in LearningPathQualitativeSignalDeduper.Deduplicate(profile.WeaknessSignals))
        {
            var label = LearningPathRules.Truncate(signal.Label, 150);
            if (label.Length == 0) continue;

            qualitativeActivities.Add(new LearningPathActivityPlan(
                LearningPathRules.QualitativeActivityKey(signal.Label),
                LearningPathValues.ResumeImprovement,
                LearningPathRules.Truncate($"Resume improvement: {label}", LearningPathRules.ActivityTitleMaxLength),
                LearningPathRules.Truncate($"Address this CV signal: {label}.", LearningPathRules.ActivityDescriptionMaxLength),
                null,
                null,
                null,
                3,
                LearningPathValues.SupportingMilestone,
                0)
            {
                LatestEvidenceAt = signal.LatestEvidenceAt
            });
        }

        var selectedStructuredActivities = activities
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.Type, StringComparer.Ordinal).First())
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.MilestoneCode, StringComparer.Ordinal)
            .ThenBy(item => item.Type, StringComparer.Ordinal)
            .ThenBy(item => item.CompetencyCode ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(LearningPathRules.MaximumCurrentActivities)
            .ToArray();

        var remainingSlots = LearningPathRules.MaximumCurrentActivities - selectedStructuredActivities.Length;
        var selectedQualitativeActivities = qualitativeActivities
            .OrderByDescending(item => item.LatestEvidenceAt)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(Math.Min(LearningPathRules.MaximumQualitativeActivities, remainingSlots));

        var orderedActivities = selectedStructuredActivities
            .Concat(selectedQualitativeActivities)
            .GroupBy(item => item.MilestoneCode, StringComparer.Ordinal)
            .SelectMany(group => group.Select((item, index) => item with { SortOrder = index }))
            .ToArray();

        var milestones = orderedActivities
            .Select(item => item.MilestoneCode)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code switch
            {
                LearningPathValues.CriticalMilestone => 1,
                LearningPathValues.DevelopingMilestone => 2,
                _ => 3
            })
            .ThenBy(code => code, StringComparer.Ordinal)
            .Select((code, index) => new LearningPathMilestonePlan(code, LearningPathRules.MilestoneTitle(code), index))
            .ToArray();

        return new LearningPathPlan(milestones, orderedActivities);
    }

    private static string? NormalizeCompetencyCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var separator = code.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == code.Length - 1) return null;
        return SkillProfileTaxonomy.CreateCode(code[..separator], code[(separator + 1)..]);
    }
}
