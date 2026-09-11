using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Recommendations;
using Nexora.Business.Skills;

namespace Nexora.Business.Progress;

public sealed record ProgressDashboardReadinessView(
    int? Score,
    int AssessedCompetencies,
    int EvidenceCount,
    int PriorityGapCount,
    int QualitativeWeaknessCount,
    DateTimeOffset? LatestEvidenceAt);

public sealed record ProgressDashboardCompetencyView(
    string Code,
    string Name,
    string Category,
    int Score,
    int EvidenceCount,
    DateTimeOffset LatestEvidenceAt);

public sealed record ProgressDashboardImprovementView(
    string Kind,
    Guid ResourceId,
    int PreviousScore,
    int CurrentScore,
    int Delta,
    DateTimeOffset At);

public sealed record ProgressDashboardWeeklyActivitiesView(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int Total,
    int ResumeAnalyses,
    int Interviews,
    int Scenarios,
    int StarAttempts,
    int LearningPathActivities);

public sealed record ProgressDashboardView(
    ProgressDashboardReadinessView Readiness,
    IReadOnlyCollection<ProgressDashboardCompetencyView> WeakestCompetencies,
    IReadOnlyCollection<ProgressDashboardImprovementView> RecentImprovements,
    ProgressDashboardWeeklyActivitiesView WeeklyCompletedActivities,
    NextPracticeRecommendationView? NextRecommendedPractice,
    ProgressView HistoricalStats);

public interface IProgressDashboardService
{
    Task<ProgressDashboardView> GetAsync(Guid userId, CancellationToken cancellationToken);
}

public static class ProgressDashboardPolicy
{
    public const int WeakestCompetencyLimit = 5;
    public const int NumericGapThreshold = LearningPathRules.NumericGapThreshold;

    public static ProgressDashboardReadinessView BuildReadiness(SkillProfileView profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var competencies = profile.Competencies.Where(item => item is not null).ToArray();
        var latestEvidenceAt = competencies.Select(item => (DateTimeOffset?)item.LatestEvidenceAt)
            .Concat(profile.WeaknessSignals.Where(item => item is not null).Select(item => (DateTimeOffset?)item.LatestEvidenceAt))
            .Max();
        var qualitativeWeaknessCount = profile.WeaknessSignals.Count(item => item is not null);

        if (competencies.Length == 0)
        {
            return new ProgressDashboardReadinessView(
                null,
                0,
                0,
                0,
                qualitativeWeaknessCount,
                latestEvidenceAt);
        }

        return new ProgressDashboardReadinessView(
            (int)Math.Round(competencies.Average(item => item.Score), MidpointRounding.AwayFromZero),
            competencies.Length,
            competencies.Sum(item => Math.Max(0, item.EvidenceCount)),
            competencies.Count(item => item.Score < NumericGapThreshold),
            qualitativeWeaknessCount,
            latestEvidenceAt);
    }

    public static IReadOnlyCollection<ProgressDashboardCompetencyView> SelectWeakest(
        SkillProfileView profile,
        int limit = WeakestCompetencyLimit)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (limit <= 0) return [];

        return profile.Competencies
            .Where(item => item is not null)
            .OrderBy(item => item.Score)
            .ThenByDescending(item => item.EvidenceCount)
            .ThenByDescending(item => item.LatestEvidenceAt)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => new ProgressDashboardCompetencyView(
                item.Code,
                item.Name,
                item.Category,
                item.Score,
                item.EvidenceCount,
                item.LatestEvidenceAt))
            .ToArray();
    }

    public static IReadOnlyCollection<ProgressDashboardImprovementView> SelectRecentInterviewImprovements(
        IEnumerable<RecentInterviewScore> scores,
        int limit = WeakestCompetencyLimit)
    {
        ArgumentNullException.ThrowIfNull(scores);
        if (limit <= 0) return [];

        var chronological = scores
            .OrderBy(item => item.CompletedAt)
            .ThenBy(item => item.InterviewId)
            .ToArray();
        var improvements = new List<ProgressDashboardImprovementView>();
        for (var index = 1; index < chronological.Length; index++)
        {
            var previous = chronological[index - 1];
            var current = chronological[index];
            if (current.Score <= previous.Score) continue;

            improvements.Add(new ProgressDashboardImprovementView(
                "interview",
                current.InterviewId,
                previous.Score,
                current.Score,
                current.Score - previous.Score,
                current.CompletedAt));
        }

        return improvements
            .OrderByDescending(item => item.At)
            .ThenBy(item => item.ResourceId)
            .Take(limit)
            .ToArray();
    }

    public static DateTimeOffset GetUtcWeekStart(DateTimeOffset now)
    {
        var utc = now.ToUniversalTime();
        var date = utc.UtcDateTime.Date;
        var daysSinceMonday = ((int)date.DayOfWeek + 6) % 7;
        return new DateTimeOffset(date.AddDays(-daysSinceMonday), TimeSpan.Zero);
    }
}
