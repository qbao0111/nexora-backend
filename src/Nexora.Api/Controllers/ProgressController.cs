using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Practice;
using Nexora.Business.Progress;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/progress")]
public sealed class ProgressController(
    IProgressService progressService,
    IProgressDashboardService progressDashboardService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<ProgressResponse>>> Get(CancellationToken cancellationToken)
    {
        var progress = await progressService.GetAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<ProgressResponse>(new ProgressResponse(
            progress.CompletedInterviews,
            progress.RecentInterviewScores.Select(s => new RecentInterviewScoreResponse(s.InterviewId, s.Score, s.CompletedAt)).ToArray(),
            progress.AverageInterviewScore,
            progress.StarAverages is null ? null : new ProgressStarAveragesResponse(progress.StarAverages.Situation, progress.StarAverages.Task, progress.StarAverages.Action, progress.StarAverages.Result),
            progress.CompletedScenarios,
            progress.AverageScenarioScore,
            progress.CompletedStarAttempts,
            progress.RecentActivity.Select(a => new RecentActivityResponse(a.Kind, a.ResourceId, a.At)).ToArray())));
    }

    [HttpGet("dashboard")]
    public async Task<ActionResult<ApiResponse<ProgressDashboardResponse>>> GetDashboard(CancellationToken cancellationToken)
    {
        var dashboard = await progressDashboardService.GetAsync(User.GetRequiredUserId(), cancellationToken);
        var historical = dashboard.HistoricalStats;
        return Ok(new ApiResponse<ProgressDashboardResponse>(new ProgressDashboardResponse(
            new ProgressDashboardReadinessResponse(
                dashboard.Readiness.Score,
                dashboard.Readiness.AssessedCompetencies,
                dashboard.Readiness.EvidenceCount,
                dashboard.Readiness.PriorityGapCount,
                dashboard.Readiness.QualitativeWeaknessCount,
                dashboard.Readiness.LatestEvidenceAt),
            dashboard.WeakestCompetencies.Select(item => new ProgressDashboardCompetencyResponse(
                item.Code,
                item.Name,
                item.Category,
                item.Score,
                item.EvidenceCount,
                item.LatestEvidenceAt)).ToArray(),
            dashboard.RecentImprovements.Select(item => new ProgressDashboardImprovementResponse(
                item.Kind,
                item.ResourceId,
                item.PreviousScore,
                item.CurrentScore,
                item.Delta,
                item.At)).ToArray(),
            new ProgressDashboardWeeklyActivitiesResponse(
                dashboard.WeeklyCompletedActivities.WindowStart,
                dashboard.WeeklyCompletedActivities.WindowEnd,
                dashboard.WeeklyCompletedActivities.Total,
                dashboard.WeeklyCompletedActivities.ResumeAnalyses,
                dashboard.WeeklyCompletedActivities.Interviews,
                dashboard.WeeklyCompletedActivities.Scenarios,
                dashboard.WeeklyCompletedActivities.StarAttempts,
                dashboard.WeeklyCompletedActivities.LearningPathActivities),
            dashboard.NextRecommendedPractice is null
                ? null
                : new NextPracticeRecommendationResponse(
                    dashboard.NextRecommendedPractice.Reason,
                    dashboard.NextRecommendedPractice.ActivityType,
                    dashboard.NextRecommendedPractice.ResourceId,
                    dashboard.NextRecommendedPractice.EstimatedMinutes,
                    dashboard.NextRecommendedPractice.Priority,
                    dashboard.NextRecommendedPractice.Action is null ? null : new NextPracticeActionResponse(
                        dashboard.NextRecommendedPractice.Action.Type,
                        dashboard.NextRecommendedPractice.Action.Reason,
                        dashboard.NextRecommendedPractice.Action.SourceInterviewId,
                        dashboard.NextRecommendedPractice.Action.SourceQuestionId,
                        dashboard.NextRecommendedPractice.Action.FocusTopic,
                        dashboard.NextRecommendedPractice.Action.SuggestedInterviewType),
                    dashboard.NextRecommendedPractice.Rationale is null ? null : new NextPracticeRecommendationRationaleResponse(
                        dashboard.NextRecommendedPractice.Rationale.CompetencyCode,
                        dashboard.NextRecommendedPractice.Rationale.CompetencyName,
                        dashboard.NextRecommendedPractice.Rationale.EvidenceCount,
                        dashboard.NextRecommendedPractice.Rationale.HasMoreRecentlyPracticedPeer)),
            new ProgressResponse(
                historical.CompletedInterviews,
                historical.RecentInterviewScores.Select(s => new RecentInterviewScoreResponse(s.InterviewId, s.Score, s.CompletedAt)).ToArray(),
                historical.AverageInterviewScore,
                historical.StarAverages is null ? null : new ProgressStarAveragesResponse(
                    historical.StarAverages.Situation,
                    historical.StarAverages.Task,
                    historical.StarAverages.Action,
                    historical.StarAverages.Result),
                historical.CompletedScenarios,
                historical.AverageScenarioScore,
                historical.CompletedStarAttempts,
                historical.RecentActivity.Select(a => new RecentActivityResponse(a.Kind, a.ResourceId, a.At)).ToArray()))));
    }
}
