using Nexora.Business.Practice;
using Nexora.Business.Progress;
using Nexora.Business.Skills;

namespace Nexora.UnitTests.Progress;

public sealed class ProgressDashboardPolicyTests
{
    [Fact]
    public void NoCompetenciesDoesNotInventReadiness()
    {
        var signalAt = At(10);

        var readiness = ProgressDashboardPolicy.BuildReadiness(
            new SkillProfileView([], [new SkillProfileWeaknessSignal("cv_analysis", "Missing SQL", signalAt)]));

        Assert.Null(readiness.Score);
        Assert.Equal(0, readiness.AssessedCompetencies);
        Assert.Equal(0, readiness.EvidenceCount);
        Assert.Equal(0, readiness.PriorityGapCount);
        Assert.Equal(1, readiness.QualitativeWeaknessCount);
        Assert.Equal(signalAt, readiness.LatestEvidenceAt);
    }

    [Fact]
    public void OneCompetencyUsesItsScore()
    {
        var readiness = ProgressDashboardPolicy.BuildReadiness(
            new SkillProfileView([Competency("resume.clarity", 68, 3, At(10))], []));

        Assert.Equal(68, readiness.Score);
        Assert.Equal(1, readiness.AssessedCompetencies);
    }

    [Fact]
    public void MultipleCompetenciesUseEqualWeightArithmeticMean()
    {
        var readiness = ProgressDashboardPolicy.BuildReadiness(
            new SkillProfileView([
                Competency("resume.clarity", 60, 2, At(10)),
                Competency("interview.structure", 70, 3, At(11)),
                Competency("scenario.problem_solving", 80, 4, At(12))
            ], []));

        Assert.Equal(70, readiness.Score);
        Assert.Equal(3, readiness.AssessedCompetencies);
        Assert.Equal(9, readiness.EvidenceCount);
    }

    [Fact]
    public void MidpointRoundingUsesAwayFromZero()
    {
        var readiness = ProgressDashboardPolicy.BuildReadiness(
            new SkillProfileView([
                Competency("resume.clarity", 1, 1, At(10)),
                Competency("resume.structure", 2, 1, At(11))
            ], []));

        Assert.Equal(2, readiness.Score);
    }

    [Fact]
    public void PriorityGapsUseTheB11StrictlyBelowSeventyFiveThreshold()
    {
        var readiness = ProgressDashboardPolicy.BuildReadiness(
            new SkillProfileView([
                Competency("resume.critical", 74, 1, At(10)),
                Competency("resume.healthy", 75, 1, At(11))
            ], []));

        Assert.Equal(1, readiness.PriorityGapCount);
    }

    [Fact]
    public void WeakestCompetenciesUseDeterministicOrderingAndLimit()
    {
        var profile = new SkillProfileView(
            [
                Competency("resume.six", 60, 1, At(1)),
                Competency("resume.five", 50, 1, At(1)),
                Competency("resume.four", 40, 1, At(1)),
                Competency("resume.three", 30, 2, At(1)),
                Competency("resume.two", 30, 2, At(2)),
                Competency("resume.one", 30, 2, At(2))
            ],
            [new SkillProfileWeaknessSignal("cv_analysis", "Qualitative only", At(12))]);

        var weakest = ProgressDashboardPolicy.SelectWeakest(profile).ToArray();

        Assert.Equal(5, weakest.Length);
        Assert.Equal(
            ["resume.one", "resume.two", "resume.three", "resume.four", "resume.five"],
            weakest.Select(item => item.Code));
        Assert.DoesNotContain(weakest, item => item.Code == "qualitative.only");
    }

    [Fact]
    public void RecentImprovementsOnlyUsePositiveConsecutiveInterviewDeltas()
    {
        var first = Id(1);
        var second = Id(2);
        var third = Id(3);
        var fourth = Id(4);

        var improvements = ProgressDashboardPolicy.SelectRecentInterviewImprovements([
            new RecentInterviewScore(third, 70, At(3)),
            new RecentInterviewScore(first, 60, At(1)),
            new RecentInterviewScore(fourth, 75, At(4)),
            new RecentInterviewScore(second, 72, At(2))
        ]).ToArray();

        Assert.Equal(2, improvements.Length);
        Assert.Equal(fourth, improvements[0].ResourceId);
        Assert.Equal(70, improvements[0].PreviousScore);
        Assert.Equal(75, improvements[0].CurrentScore);
        Assert.Equal(5, improvements[0].Delta);
        Assert.Equal(second, improvements[1].ResourceId);
        Assert.Equal(12, improvements[1].Delta);
    }

    [Fact]
    public void EqualOrLowerInterviewScoresDoNotCreateImprovements()
    {
        var improvements = ProgressDashboardPolicy.SelectRecentInterviewImprovements([
            new RecentInterviewScore(Id(1), 80, At(1)),
            new RecentInterviewScore(Id(2), 80, At(2)),
            new RecentInterviewScore(Id(3), 70, At(3))
        ]);

        Assert.Empty(improvements);
    }

    [Fact]
    public void RepeatedCalculationsAreDeterministic()
    {
        var profile = new SkillProfileView([
            Competency("resume.zed", 42, 2, At(1)),
            Competency("resume.alpha", 42, 2, At(1))
        ], [new SkillProfileWeaknessSignal("cv_analysis", "SQL", At(2))]);

        Assert.Equal(ProgressDashboardPolicy.BuildReadiness(profile), ProgressDashboardPolicy.BuildReadiness(profile));
        Assert.Equal(ProgressDashboardPolicy.SelectWeakest(profile), ProgressDashboardPolicy.SelectWeakest(profile));
    }

    [Fact]
    public void UtcWeekStartHandlesMondaySundayAndOffsets()
    {
        var cases = new[]
        {
            (new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)),
            (new DateTimeOffset(2026, 9, 20, 23, 59, 59, TimeSpan.FromHours(7)), new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero)),
            (new DateTimeOffset(2026, 9, 16, 10, 0, 0, TimeSpan.FromHours(7)), new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero))
        };

        foreach (var (input, expected) in cases)
            Assert.Equal(expected, ProgressDashboardPolicy.GetUtcWeekStart(input));
    }

    private static SkillProfileCompetency Competency(
        string code,
        int score,
        int evidenceCount,
        DateTimeOffset latestEvidenceAt) =>
        new(code, code[(code.IndexOf('.') + 1)..], code[..code.IndexOf('.')], score, evidenceCount, latestEvidenceAt, []);

    private static DateTimeOffset At(int day) => new(2026, 9, day, 0, 0, 0, TimeSpan.Zero);

    private static Guid Id(int value) => Guid.Parse($"80000000-0000-0000-0000-{value:000000000001}");
}
