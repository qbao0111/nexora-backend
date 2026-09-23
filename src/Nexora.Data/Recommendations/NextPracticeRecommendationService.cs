using Microsoft.EntityFrameworkCore;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Recommendations;
using Nexora.Business.Skills;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.Data.Recommendations;

public sealed class NextPracticeRecommendationService(
    ILearningPathService learningPathService,
    ISkillProfileService skillProfileService,
    NexoraDbContext dbContext) : INextPracticeRecommendationService
{
    public async Task<NextPracticeRecommendationView?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var learningPath = await learningPathService.GetAsync(userId, cancellationToken);
        var skillProfile = await skillProfileService.GetAsync(userId, cancellationToken);
        return await SelectAsync(userId, learningPath, skillProfile, cancellationToken);
    }

    public async Task<NextPracticeRecommendationView?> GetAsync(Guid userId, SkillProfileView skillProfile, CancellationToken cancellationToken)
    {
        var learningPath = await learningPathService.GetAsync(userId, cancellationToken);
        return await SelectAsync(userId, learningPath, skillProfile, cancellationToken);
    }

    private async Task<NextPracticeRecommendationView?> SelectAsync(Guid userId, LearningPathView learningPath, SkillProfileView skillProfile, CancellationToken cancellationToken)
    {
        var recommendation = NextPracticeRecommendationPolicy.Select(learningPath, skillProfile);
        if (recommendation?.ActivityType != LearningPathValues.Interview || recommendation.Action is null)
            return recommendation;

        var sourceQuery = dbContext.InterviewSessions.AsNoTracking()
            .Where(session => session.UserId == userId &&
                              session.Status == PracticeValues.Completed &&
                              dbContext.InterviewReports.Any(report =>
                                  report.UserId == userId && report.InterviewSessionId == session.Id));
        var requestedSourceId = recommendation.Action.SourceInterviewId;
        if (requestedSourceId is { } sourceId)
            sourceQuery = sourceQuery.Where(item => item.Id == sourceId);

        var source = await SelectSourceAsync(sourceQuery, cancellationToken);
        if (source is null)
        {
            sourceQuery = dbContext.InterviewSessions.AsNoTracking()
                .Where(session => session.UserId == userId &&
                                  session.Status == PracticeValues.Completed &&
                                  dbContext.InterviewReports.Any(report =>
                                      report.UserId == userId && report.InterviewSessionId == session.Id));
            source = await SelectSourceAsync(sourceQuery, cancellationToken);
        }

        if (source is null) return recommendation with { Action = null };
        return recommendation with
        {
            Action = recommendation.Action with
            {
                SourceInterviewId = source.Id,
                SuggestedInterviewType = source.InterviewType,
                FocusTopic = recommendation.Action.FocusTopic ?? InterviewQuestionValues.TopicForInterviewType(source.InterviewType)
            }
        };
    }

    private sealed record InterviewSource(Guid Id, string InterviewType, DateTimeOffset CreatedAt);

    private async Task<InterviewSource?> SelectSourceAsync(
        IQueryable<InterviewSession> sourceQuery,
        CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
        {
            return await sourceQuery
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Select(item => new InterviewSource(item.Id, item.InterviewType, item.CreatedAt))
                .FirstOrDefaultAsync(cancellationToken);
        }

        // SQLite cannot order DateTimeOffset in SQL. The test provider keeps
        // this deterministic fallback on the scalar key; PostgreSQL uses the
        // production CreatedAt ordering above.
        return await sourceQuery
            .OrderByDescending(item => item.Id)
            .Select(item => new InterviewSource(item.Id, item.InterviewType, item.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
