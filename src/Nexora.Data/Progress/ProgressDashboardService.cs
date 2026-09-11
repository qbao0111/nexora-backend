using Microsoft.EntityFrameworkCore;
using Nexora.Business.Common;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Progress;
using Nexora.Business.Recommendations;
using Nexora.Business.Skills;
using Nexora.Data.Persistence;

namespace Nexora.Data.Progress;

public sealed class ProgressDashboardService(
    IProgressService progressService,
    ISkillProfileService skillProfileService,
    INextPracticeRecommendationService nextPracticeRecommendationService,
    NexoraDbContext dbContext,
    TimeProvider timeProvider) : IProgressDashboardService
{
    public async Task<ProgressDashboardView> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var historicalStats = await progressService.GetAsync(userId, cancellationToken);
        var profile = await skillProfileService.GetAsync(userId, cancellationToken);
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        var windowStart = ProgressDashboardPolicy.GetUtcWeekStart(now);
        var weekly = await GetWeeklyActivitiesAsync(userId, windowStart, now, cancellationToken);
        var nextRecommendation = await GetNextRecommendationAsync(userId, cancellationToken);

        return new ProgressDashboardView(
            ProgressDashboardPolicy.BuildReadiness(profile),
            ProgressDashboardPolicy.SelectWeakest(profile),
            ProgressDashboardPolicy.SelectRecentInterviewImprovements(historicalStats.RecentInterviewScores),
            weekly,
            nextRecommendation,
            historicalStats);
    }

    private async Task<ProgressDashboardWeeklyActivitiesView> GetWeeklyActivitiesAsync(
        Guid userId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var resumeAnalyses = await dbContext.ResumeAnalyses.AsNoTracking()
            .CountAsync(item => item.UserId == userId && item.Status == PracticeValues.Completed &&
                                item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd, cancellationToken);
        var interviews = await dbContext.InterviewSessions.AsNoTracking()
            .CountAsync(item => item.UserId == userId && item.Status == PracticeValues.Completed &&
                                item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd, cancellationToken);
        var scenarios = await dbContext.ScenarioAttempts.AsNoTracking()
            .CountAsync(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed &&
                                item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd, cancellationToken);
        var starAttempts = await dbContext.StarAttempts.AsNoTracking()
            .CountAsync(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed &&
                                item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd, cancellationToken);
        var learningPathActivities = await dbContext.LearningPathActivities.AsNoTracking()
            .CountAsync(item => item.LearningPath.UserId == userId && item.Status == LearningPathValues.Completed &&
                                item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd, cancellationToken);

        return new ProgressDashboardWeeklyActivitiesView(
            windowStart,
            windowEnd,
            resumeAnalyses + interviews + scenarios + starAttempts + learningPathActivities,
            resumeAnalyses,
            interviews,
            scenarios,
            starAttempts,
            learningPathActivities);
    }

    private async Task<NextPracticeRecommendationView?> GetNextRecommendationAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await nextPracticeRecommendationService.GetAsync(userId, cancellationToken);
        }
        catch (BusinessException exception) when (
            exception.Code is "ACTIVE_CAREER_GOAL_REQUIRED" or "LEARNING_PATH_NOT_FOUND")
        {
            return null;
        }
    }
}
