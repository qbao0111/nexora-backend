using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Nexora.Business.Platform;
using Nexora.Business.Practice;
using Nexora.Data.Feedback;
using Nexora.Data.Persistence;

namespace Nexora.Data.Platform;

public sealed class PlatformStatsService(
    NexoraDbContext dbContext,
    IMemoryCache cache) : IPlatformStatsService
{
    private const string CacheKey = "public-platform-stats";

    public async Task<PlatformStatsView> GetAsync(CancellationToken cancellationToken) =>
        (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);

            var userCount = await dbContext.Users.AsNoTracking()
                .CountAsync(user => user.IsActive &&
                                    user.DeletedAt == null &&
                                    user.DeletionRequestedAt == null,
                    cancellationToken);
            var completedInterviewCount = await dbContext.InterviewSessions.AsNoTracking()
                .CountAsync(session => session.Status == PracticeValues.Completed &&
                                       session.CompletedAt != null,
                    cancellationToken);
            var completedCvAnalysisCount = await dbContext.ResumeAnalyses.AsNoTracking()
                .CountAsync(analysis => analysis.Status == PracticeValues.Completed &&
                                        analysis.CompletedAt != null,
                    cancellationToken);
            var feedback = await PublicFeedbackEligibility.Query(dbContext)
                .GroupBy(_ => 1)
                .Select(group => new
                {
                    RatingCount = group.Count(),
                    AverageRating = (double?)group.Average(item => item.Rating)
                })
                .SingleOrDefaultAsync(cancellationToken);

            return new PlatformStatsView(
                userCount,
                completedInterviewCount,
                completedCvAnalysisCount,
                feedback?.AverageRating,
                feedback?.RatingCount ?? 0);
        }))!;
}
