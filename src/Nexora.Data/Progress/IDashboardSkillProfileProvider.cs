using Nexora.Business.Skills;

namespace Nexora.Data.Progress;

internal interface IDashboardSkillProfileProvider
{
    Task<SkillProfileView> GetFromSnapshotAsync(Guid userId, DashboardEvidenceSnapshot snapshot, CancellationToken cancellationToken);
}
