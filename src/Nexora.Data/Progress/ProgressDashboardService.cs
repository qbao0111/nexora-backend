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
        var nextRecommendation = await GetNextRecommendationAsync(userId, profile, cancellationToken);

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
        if (string.Equals(
                dbContext.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.Sqlite",
                StringComparison.Ordinal))
        {
            return await GetWeeklyActivitiesFromSqliteAsync(userId, windowStart, windowEnd, cancellationToken);
        }

        var counts = await dbContext.Users.AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(_ => new
            {
                ResumeAnalyses = dbContext.ResumeAnalyses.Count(item => item.UserId == userId && item.Status == PracticeValues.Completed &&
                    item.CompletedAt != null && item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd),
                Interviews = dbContext.InterviewSessions.Count(item => item.UserId == userId && item.Status == PracticeValues.Completed &&
                    item.CompletedAt != null && item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd),
                Scenarios = dbContext.ScenarioAttempts.Count(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed &&
                    item.CompletedAt != null && item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd),
                StarAttempts = dbContext.StarAttempts.Count(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed &&
                    item.CompletedAt != null && item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd),
                LearningPathActivities = dbContext.LearningPathActivities.Count(item => item.LearningPath.UserId == userId &&
                    item.Status == LearningPathValues.Completed && item.CompletedAt != null &&
                    item.CompletedAt >= windowStart && item.CompletedAt <= windowEnd)
            })
            .SingleAsync(cancellationToken);

        return new ProgressDashboardWeeklyActivitiesView(
            windowStart,
            windowEnd,
            counts.ResumeAnalyses + counts.Interviews + counts.Scenarios + counts.StarAttempts + counts.LearningPathActivities,
            counts.ResumeAnalyses,
            counts.Interviews,
            counts.Scenarios,
            counts.StarAttempts,
            counts.LearningPathActivities);
    }

    private async Task<ProgressDashboardWeeklyActivitiesView> GetWeeklyActivitiesFromSqliteAsync(
        Guid userId,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var resumeAnalyses = await CountSqliteCompletionsAsync(
            dbContext.ResumeAnalyses.AsNoTracking()
                .Where(item => item.UserId == userId && item.Status == PracticeValues.Completed && item.CompletedAt != null)
                .Select(item => item.CompletedAt),
            windowStart,
            windowEnd,
            cancellationToken);
        var interviews = await CountSqliteCompletionsAsync(
            dbContext.InterviewSessions.AsNoTracking()
                .Where(item => item.UserId == userId && item.Status == PracticeValues.Completed && item.CompletedAt != null)
                .Select(item => item.CompletedAt),
            windowStart,
            windowEnd,
            cancellationToken);
        var scenarios = await CountSqliteCompletionsAsync(
            dbContext.ScenarioAttempts.AsNoTracking()
                .Where(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed && item.CompletedAt != null)
                .Select(item => item.CompletedAt),
            windowStart,
            windowEnd,
            cancellationToken);
        var starAttempts = await CountSqliteCompletionsAsync(
            dbContext.StarAttempts.AsNoTracking()
                .Where(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed && item.CompletedAt != null)
                .Select(item => item.CompletedAt),
            windowStart,
            windowEnd,
            cancellationToken);
        var learningPathActivities = await CountSqliteCompletionsAsync(
            dbContext.LearningPathActivities.AsNoTracking()
                .Where(item => item.LearningPath.UserId == userId && item.Status == LearningPathValues.Completed && item.CompletedAt != null)
                .Select(item => item.CompletedAt),
            windowStart,
            windowEnd,
            cancellationToken);

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

    private static async Task<int> CountSqliteCompletionsAsync(
        IQueryable<DateTimeOffset?> completionTimes,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken)
    {
        var completedAtValues = await completionTimes.ToListAsync(cancellationToken);
        return completedAtValues.Count(completedAt =>
            completedAt is not null && completedAt.Value >= windowStart && completedAt.Value <= windowEnd);
    }

    private async Task<NextPracticeRecommendationView?> GetNextRecommendationAsync(
        Guid userId,
        SkillProfileView skillProfile,
        CancellationToken cancellationToken)
    {
        try
        {
            return await nextPracticeRecommendationService.GetAsync(userId, skillProfile, cancellationToken);
        }
        catch (BusinessException exception) when (
            exception.Code is "ACTIVE_CAREER_GOAL_REQUIRED" or "LEARNING_PATH_NOT_FOUND")
        {
            return null;
        }
    }
}
