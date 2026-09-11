using Microsoft.EntityFrameworkCore;
using Nexora.Business.Practice;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class NpgsqlQueryTranslationTests
{
    [Fact]
    public void ProgressQueriesTranslateWithNpgsqlWithoutConnectingToAStore()
    {
        var options = new DbContextOptionsBuilder<NexoraDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_probe;Username=probe;Password=probe")
            .Options;
        using var db = new NexoraDbContext(options);
        var userId = Guid.NewGuid();

        var recentScores = db.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedAt)
            .Take(10)
            .Select(item => new RecentInterviewScore(item.InterviewSessionId, item.OverallScore, item.CreatedAt));
        var recentInterviews = db.InterviewSessions.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.UpdatedAt })
            .OrderByDescending(item => item.UpdatedAt)
            .Take(5);
        var recentScenarioAttempts = db.ScenarioAttempts.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.UpdatedAt })
            .OrderByDescending(item => item.UpdatedAt)
            .Take(5);
        var recentStarAttempts = db.StarAttempts.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.UpdatedAt })
            .OrderByDescending(item => item.UpdatedAt)
            .Take(5);

        _ = recentScores.ToQueryString();
        _ = recentInterviews.ToQueryString();
        _ = recentScenarioAttempts.ToQueryString();
        _ = recentStarAttempts.ToQueryString();
    }
}
