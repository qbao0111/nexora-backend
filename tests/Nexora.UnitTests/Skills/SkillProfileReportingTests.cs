using Nexora.Business.Skills;

namespace Nexora.UnitTests.Skills;

public sealed class SkillProfileReportingTests
{
    [Fact]
    public void ReportingIdentityIsStableOwnerScopedAndChangesWithContent()
    {
        var owner = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        var profile = SkillProfileAggregator.Aggregate([
            new SkillProfileEvidence("one", "behavioral.action", "Action", "behavioral", "star_attempt", 70, at)]);
        var id = SkillProfileReporting.CreateId(owner, profile);
        Assert.NotNull(id);
        Assert.Equal(id, SkillProfileReporting.CreateId(owner, profile));
        Assert.NotEqual(id, SkillProfileReporting.CreateId(Guid.NewGuid(), profile));
        Assert.Equal(id, SkillProfileReporting.CreateId(owner, profile with { ReportingId = Guid.NewGuid() }));
        Assert.NotEqual(id, SkillProfileReporting.CreateId(owner,
            profile with { Competencies = [profile.Competencies.Single() with { Score = 71 }] }));
        Assert.Null(SkillProfileReporting.CreateId(owner, SkillProfileAggregator.Aggregate([])));
    }
}
