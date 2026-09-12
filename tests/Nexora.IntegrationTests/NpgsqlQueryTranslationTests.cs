using Microsoft.EntityFrameworkCore;
using Nexora.Business.Billing;
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

        var scenarioPattern = "%flash%";
        var scenarios = db.Scenarios.AsNoTracking()
            .Where(item => item.Status == PracticeFeatureValues.Published)
            .Where(item => EF.Functions.ILike(item.Title, scenarioPattern, "\\") ||
                           EF.Functions.ILike(item.Summary, scenarioPattern, "\\"))
            .OrderBy(item => item.SortOrder)
            .ThenBy(item => item.Title)
            .Take(20)
            .Select(item => item.Id);

        _ = scenarios.ToQueryString();
    }

    [Fact]
    public void DashboardSummaryQueriesTranslateWithNpgsqlWithoutConnectingToAStore()
    {
        var options = new DbContextOptionsBuilder<NexoraDbContext>()
            .UseNpgsql("Host=localhost;Database=translation_probe;Username=probe;Password=probe")
            .Options;
        using var db = new NexoraDbContext(options);
        var userId = Guid.NewGuid();

        var activeEntitlements = db.Entitlements.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == BillingValues.Active)
            .Select(item => new
            {
                item.Id,
                item.PlanCodeSnapshot,
                item.Status,
                item.InterviewLimit,
                item.Reserved,
                item.Consumed,
                item.Adjustment,
                item.StartsAt,
                item.EndsAt
            });
        var entitlementIds = new[] { Guid.NewGuid() };
        var featureRows = db.EntitlementFeatures.AsNoTracking()
            .Where(item => entitlementIds.Contains(item.EntitlementId))
            .Select(item => new
            {
                item.EntitlementId,
                item.FeatureCode,
                item.FeatureDefinitionId,
                item.IsEnabled,
                item.Limit,
                item.Reserved,
                item.Consumed,
                item.Adjustment
            });
        var orders = db.Orders.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(20)
            .Select(item => new OrderView(item.Id, item.PlanCodeSnapshot, item.AmountMinor, item.Currency, item.Status, item.CreatedAt));
        var interviews = db.InterviewSessions.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .Take(20)
            .Select(item => new InterviewSummary(item.Id, item.Role, item.Status, item.UpdatedAt));
        var reports = db.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId)
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(20)
            .Select(item => new ReportSummary(item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt));

        _ = activeEntitlements.ToQueryString();
        _ = featureRows.ToQueryString();
        _ = orders.ToQueryString();
        _ = interviews.ToQueryString();
        _ = reports.ToQueryString();
    }
}
