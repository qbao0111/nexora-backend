namespace Nexora.Api.Contracts;

public sealed record ProgressDashboardResponse(
    ProgressDashboardReadinessResponse Readiness,
    IReadOnlyCollection<ProgressDashboardCompetencyResponse> WeakestCompetencies,
    IReadOnlyCollection<ProgressDashboardImprovementResponse> RecentImprovements,
    ProgressDashboardWeeklyActivitiesResponse WeeklyCompletedActivities,
    NextPracticeRecommendationResponse? NextRecommendedPractice,
    ProgressResponse HistoricalStats);

public sealed record ProgressDashboardReadinessResponse(
    int? Score,
    int AssessedCompetencies,
    int EvidenceCount,
    int PriorityGapCount,
    int QualitativeWeaknessCount,
    DateTimeOffset? LatestEvidenceAt);

public sealed record ProgressDashboardCompetencyResponse(
    string Code,
    string Name,
    string Category,
    int Score,
    int EvidenceCount,
    DateTimeOffset LatestEvidenceAt);

public sealed record ProgressDashboardImprovementResponse(
    string Kind,
    Guid ResourceId,
    int PreviousScore,
    int CurrentScore,
    int Delta,
    DateTimeOffset At);

public sealed record ProgressDashboardWeeklyActivitiesResponse(
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int Total,
    int ResumeAnalyses,
    int Interviews,
    int Scenarios,
    int StarAttempts,
    int LearningPathActivities);
