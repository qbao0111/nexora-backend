using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Privacy;
using Nexora.Data.Persistence;

namespace Nexora.Api.Health;

public sealed class OperationsHealthOptions
{
    public const string SectionName = "OperationsHealth";
    public int MaxQueueLagMinutes { get; set; } = 10;
    public int MaxPaymentPendingMinutes { get; set; } = 60;
    public int RecentFailureWindowMinutes { get; set; } = 15;
}

public sealed class OperationsHealthCheck(
    NexoraDbContext dbContext,
    IOptions<OperationsHealthOptions> options,
    TimeProvider timeProvider) : IHealthCheck
{
    private static readonly string[] JobTypes =
        ["ResumeExtractionRequested", "ResumeAnalysisRequested", "InterviewStartRequested", "InterviewReportRequested"];

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var settings = options.Value;
        var staleJobs = await CountAsync(
            dbContext.OutboxEvents.AsNoTracking().Where(item => item.Status == BillingValues.Pending && JobTypes.Contains(item.Type))
                .Select(item => item.CreatedAt),
            now.AddMinutes(-settings.MaxQueueLagMinutes), cancellationToken);
        var stalePayments = await CountAsync(
            dbContext.Orders.AsNoTracking().Where(item => item.Status == BillingValues.Pending).Select(item => item.CreatedAt),
            now.AddMinutes(-settings.MaxPaymentPendingMinutes), cancellationToken);
        var recentJobFailures = await CountAsync(
            dbContext.OutboxEvents.AsNoTracking().Where(item => item.Status == PrivacyValues.Failed && item.ProcessedAt != null &&
                    JobTypes.Contains(item.Type))
                .Select(item => item.ProcessedAt!.Value),
            now.AddMinutes(-settings.RecentFailureWindowMinutes), cancellationToken, newerThan: true);
        var recentDeletionFailures = await CountAsync(
            dbContext.DataPrivacyRequests.AsNoTracking().Where(item => item.Status == PrivacyValues.Failed).Select(item => item.UpdatedAt),
            now.AddMinutes(-settings.RecentFailureWindowMinutes), cancellationToken, newerThan: true);

        if (staleJobs + stalePayments + recentJobFailures + recentDeletionFailures == 0)
            return HealthCheckResult.Healthy();
        return HealthCheckResult.Degraded("Operational thresholds exceeded.", data: new Dictionary<string, object>
        {
            ["staleJobs"] = staleJobs,
            ["stalePayments"] = stalePayments,
            ["recentJobFailures"] = recentJobFailures,
            ["recentDeletionFailures"] = recentDeletionFailures
        });
    }

    private async Task<int> CountAsync(
        IQueryable<DateTimeOffset> timestamps,
        DateTimeOffset threshold,
        CancellationToken cancellationToken,
        bool newerThan = false)
    {
        if (dbContext.Database.IsNpgsql())
            return newerThan
                ? await timestamps.CountAsync(value => value >= threshold, cancellationToken)
                : await timestamps.CountAsync(value => value <= threshold, cancellationToken);
        var values = await timestamps.ToArrayAsync(cancellationToken);
        return values.Count(value => newerThan ? value >= threshold : value <= threshold);
    }
}
