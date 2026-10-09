using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexora.Business.Ai;
using Nexora.Business.Common;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed class PaidJobQueueAdmission(NexoraDbContext db, IOptions<ProviderBudgetOptions> options)
{
    private static readonly string[] PaidTypes =
    [
        "ResumeAnalysisRequested", "InterviewStartRequested", "InterviewQuestionPlanRequested",
        "InterviewAnswerEvaluationRequested", "InterviewReportRequested", "ScenarioEvaluationRequested", "StarEvaluationRequested"
    ];

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var incoming = db.ChangeTracker.Entries<Nexora.Data.Billing.OutboxEvent>()
            .Count(entry => entry.State == EntityState.Added && PaidTypes.Contains(entry.Entity.Type, StringComparer.Ordinal));
        if (incoming == 0) return;
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Paid work admission requires the enqueue transaction.");
        if (db.Database.IsNpgsql())
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(782346110)", cancellationToken);
        var depth = await db.OutboxEvents.CountAsync(item => PaidTypes.Contains(item.Type) &&
            (item.Status == "pending" || item.Status == "processing"), cancellationToken);
        if (depth + incoming > options.Value.MaximumQueuedJobs)
            throw new BusinessException("AI_QUEUE_FULL", "Hàng đợi AI đang đầy. Vui lòng thử lại sau.", BusinessErrorKind.RateLimited);
    }
}
