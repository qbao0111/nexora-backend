using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Skills;

namespace Nexora.Business.Recommendations;

public sealed record NextPracticeActionView(
    string Type,
    string Reason,
    Guid? SourceInterviewId,
    Guid? SourceQuestionId,
    string? FocusTopic,
    string? SuggestedInterviewType);

public sealed record NextPracticeRecommendationRationaleView(
    string CompetencyName,
    int EvidenceCount,
    bool HasMoreRecentlyPracticedPeer);

public sealed record NextPracticeRecommendationView(
    string Reason,
    string ActivityType,
    Guid? ResourceId,
    int EstimatedMinutes,
    int Priority,
    NextPracticeActionView? Action = null,
    NextPracticeRecommendationRationaleView? Rationale = null);

public interface INextPracticeRecommendationService
{
    Task<NextPracticeRecommendationView?> GetAsync(Guid userId, CancellationToken cancellationToken);
}

public static class NextPracticeDurationPolicy
{
    public static bool TryGetEstimatedMinutes(string? activityType, out int estimatedMinutes)
    {
        estimatedMinutes = activityType switch
        {
            LearningPathValues.Scenario => 20,
            LearningPathValues.StarDrill => 15,
            LearningPathValues.Interview => 20,
            LearningPathValues.ResumeImprovement => 15,
            LearningPathValues.ExternalLearning => 20,
            _ => 0
        };

        return estimatedMinutes > 0;
    }
}

public static class NextPracticeRecommendationPolicy
{
    private sealed record Candidate(
        LearningPathActivityView Activity,
        string? CompetencyCode,
        string? CompetencyName,
        int EvidenceCount,
        DateTimeOffset LastPracticeAt,
        int? Score,
        int MilestoneSortOrder,
        bool IsScored,
        int EstimatedMinutes);

    public static NextPracticeRecommendationView? Select(
        LearningPathView learningPath,
        SkillProfileView skillProfile)
    {
        ArgumentNullException.ThrowIfNull(learningPath);
        ArgumentNullException.ThrowIfNull(skillProfile);

        var competencies = skillProfile.Competencies
            .Where(item => item is not null)
            .Select(item => new { Competency = item, Code = NormalizeCode(item.Code) })
            .Where(item => item.Code is not null)
            .GroupBy(item => item.Code!, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => item.Competency.Score)
                .ThenByDescending(item => item.Competency.EvidenceCount)
                .ThenBy(item => item.Competency.LatestEvidenceAt)
                .ThenBy(item => item.Competency.Name, StringComparer.Ordinal)
                .First())
            .ToDictionary(item => item.Code!, item => item.Competency, StringComparer.Ordinal);

        var completedPracticeByCode = learningPath.Milestones
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .SelectMany(item => item.Activities
                .OrderBy(activity => activity.SortOrder)
                .ThenBy(activity => activity.Id))
            .Where(activity => activity.Status == LearningPathValues.Completed &&
                               activity.CompletedAt.HasValue)
            .Select(activity => new { Code = NormalizeCode(activity.CompetencyCode), activity.CompletedAt })
            .Where(item => item.Code is not null)
            .GroupBy(item => item.Code!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(item => item.CompletedAt!.Value), StringComparer.Ordinal);

        var candidates = learningPath.Milestones
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ThenBy(item => item.Id)
            .SelectMany(milestone => milestone.Activities
                .OrderBy(activity => activity.SortOrder)
                .ThenBy(activity => activity.Id)
                .Select(activity => CreateCandidate(
                    activity,
                    milestone.SortOrder,
                    competencies,
                    completedPracticeByCode)))
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .OrderBy(candidate => candidate.Activity.Priority)
            .ThenByDescending(candidate => candidate.EvidenceCount)
            .ThenBy(candidate => candidate.LastPracticeAt)
            .ThenBy(candidate => candidate.Score.HasValue ? 0 : 1)
            .ThenBy(candidate => candidate.Score ?? int.MaxValue)
            .ThenBy(candidate => candidate.MilestoneSortOrder)
            .ThenBy(candidate => candidate.Activity.SortOrder)
            .ThenBy(candidate => candidate.Activity.Id)
            .ToArray();

        var selected = candidates.FirstOrDefault();
        if (selected is null) return null;
        var hasMoreRecentlyPracticedPeer = HasMoreRecentlyPracticedPeer(selected, candidates);

        return new NextPracticeRecommendationView(
            BuildReason(selected, hasMoreRecentlyPracticedPeer),
            selected.Activity.Type,
            selected.Activity.ResourceId,
            selected.EstimatedMinutes,
            selected.Activity.Priority,
            selected.Activity.Type == LearningPathValues.Interview
                ? new NextPracticeActionView(
                    "practice_again",
                    InterviewPracticeValues.Recommendation,
                    selected.Activity.ResourceId,
                    null,
                    FocusTopicForInterviewCompetency(selected.CompetencyCode),
                    null)
                : null,
            selected.IsScored
                ? new NextPracticeRecommendationRationaleView(
                    selected.CompetencyName ?? selected.Activity.Title,
                    selected.EvidenceCount,
                    hasMoreRecentlyPracticedPeer)
                : null);
    }

    private static Candidate? CreateCandidate(
        LearningPathActivityView activity,
        int milestoneSortOrder,
        Dictionary<string, SkillProfileCompetency> competencies,
        Dictionary<string, DateTimeOffset> completedPracticeByCode)
    {
        if (activity.Status != LearningPathValues.Pending ||
            !NextPracticeDurationPolicy.TryGetEstimatedMinutes(activity.Type, out var estimatedMinutes))
            return null;

        var code = NormalizeCode(activity.CompetencyCode);
        if (code is not null)
        {
            if (!competencies.TryGetValue(code, out var competency) ||
                competency.Score >= LearningPathRules.NumericGapThreshold)
                return null;

            var lastPracticeAt = competency.LatestEvidenceAt;
            if (completedPracticeByCode.TryGetValue(code, out var completedAt) && completedAt > lastPracticeAt)
                lastPracticeAt = completedAt;

            return new Candidate(
                activity,
                code,
                string.IsNullOrWhiteSpace(competency.Name) ? activity.Title : competency.Name,
                Math.Max(0, competency.EvidenceCount),
                lastPracticeAt,
                competency.Score,
                milestoneSortOrder,
                true,
                estimatedMinutes);
        }

        return new Candidate(
            activity,
            null,
            null,
            0,
            activity.CreatedAt,
            null,
            milestoneSortOrder,
            false,
            estimatedMinutes);
    }

    private static string BuildReason(Candidate selected, bool hasMoreRecentlyPracticedPeer)
    {
        if (!selected.IsScored)
            return "Address this resume improvement next because no higher-priority evidence-backed practice activity is currently pending.";

        var name = string.IsNullOrWhiteSpace(selected.CompetencyName) ? "this competency" : selected.CompetencyName;
        var evidenceLabel = selected.EvidenceCount == 1 ? "evidence item" : "evidence items";
        var reason = $"Practice {name} next because it is a priority {selected.Activity.Priority} gap supported by {selected.EvidenceCount} {evidenceLabel}.";
        return hasMoreRecentlyPracticedPeer
            ? $"{reason} It has not been practiced as recently as another current-priority gap."
            : reason;
    }

    private static bool HasMoreRecentlyPracticedPeer(Candidate selected, IReadOnlyCollection<Candidate> candidates) =>
        selected.IsScored && candidates.Any(candidate =>
            candidate.IsScored &&
            candidate.Activity.Id != selected.Activity.Id &&
            candidate.Activity.Priority == selected.Activity.Priority &&
            candidate.LastPracticeAt > selected.LastPracticeAt);

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var separator = code.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == code.Length - 1) return null;
        return SkillProfileTaxonomy.CreateCode(code[..separator], code[(separator + 1)..]);
    }

    private static string? FocusTopicForInterviewCompetency(string? competencyCode)
    {
        if (string.IsNullOrWhiteSpace(competencyCode)) return null;
        var separator = competencyCode.IndexOf('.', StringComparison.Ordinal);
        if (separator < 0 || separator == competencyCode.Length - 1) return null;
        var focus = competencyCode[(separator + 1)..].ToLowerInvariant();
        return focus switch
        {
            InterviewQuestionValues.SelfIntroduction or
            InterviewQuestionValues.BehavioralStar or
            InterviewQuestionValues.MotivationRoleFit or
            InterviewQuestionValues.Technical or
            InterviewQuestionValues.Behavioral or
            InterviewQuestionValues.CvTargeted or
            InterviewQuestionValues.JdTargeted or
            InterviewQuestionValues.Scenario or
            "correctness" or "structure" or "completeness" or "clarity" => focus,
            _ => null
        };
    }
}
