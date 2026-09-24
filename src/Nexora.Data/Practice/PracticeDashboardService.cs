using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed class PracticeDashboardService(NexoraDbContext dbContext, IBillingService billingService) : IPracticeDashboardService
{
    public async Task<DashboardView> GetDashboardAsync(Guid userId, CancellationToken cancellationToken)
    {
        var billing = await billingService.GetSummaryAsync(userId, cancellationToken);
        var isSqlite = string.Equals(
            dbContext.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.Sqlite",
            StringComparison.Ordinal);
        var interviewRowsQuery = dbContext.InterviewSessions.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.Role, item.Status, item.UpdatedAt });
        var interviewRows = isSqlite
            ? await dbContext.InterviewSessions
                .FromSqlInterpolated($"SELECT * FROM interview_sessions WHERE \"UserId\" = {userId} ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT 20")
                .AsNoTracking()
                .Select(item => new { item.Id, item.Role, item.Status, item.UpdatedAt })
                .ToArrayAsync(cancellationToken)
            : await interviewRowsQuery
                .OrderByDescending(item => item.UpdatedAt)
                .ThenByDescending(item => item.Id)
                .Take(20)
                .ToArrayAsync(cancellationToken);
        var interviews = interviewRows
            .Select(item => new InterviewSummary(item.Id, item.Role, item.Status, item.UpdatedAt))
            .ToArray();

        var reportRowsQuery = dbContext.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new { item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt });
        var reportRows = isSqlite
            ? await dbContext.InterviewReports
                .FromSqlInterpolated($"SELECT * FROM interview_reports WHERE \"UserId\" = {userId} ORDER BY \"CreatedAt\" DESC, \"Id\" DESC LIMIT 20")
                .AsNoTracking()
                .Select(item => new { item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt })
                .ToArrayAsync(cancellationToken)
            : await reportRowsQuery
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(20)
                .ToArrayAsync(cancellationToken);
        var reports = reportRows
            .Select(item => new ReportSummary(item.Id, item.InterviewSessionId, item.OverallScore, item.CreatedAt))
            .ToArray();
        return new DashboardView(billing, interviews, reports);
    }


}