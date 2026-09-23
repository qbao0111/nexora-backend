using Nexora.Business.Practice;

namespace Nexora.Data.Progress;

internal interface IDashboardProgressProvider
{
    Task<(ProgressView View, DashboardEvidenceSnapshot Snapshot)> GetDashboardAsync(Guid userId, CancellationToken cancellationToken);
}
