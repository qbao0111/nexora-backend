using Nexora.Business.Skills;

namespace Nexora.UnitTests.Skills;

public sealed class SkillProfileAggregatorTests
{
    [Fact]
    public void OneScoreReturnsItselfWithTraceability()
    {
        var at = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);

        var profile = SkillProfileAggregator.Aggregate([
            Evidence("resume-analysis:1", "resume.clarity", "Clarity", "resume", "cv_analysis", 73, at)
        ]);

        var competency = Assert.Single(profile.Competencies);
        Assert.Equal("resume.clarity", competency.Code);
        Assert.Equal(73, competency.Score);
        Assert.Equal(1, competency.EvidenceCount);
        Assert.Equal(at, competency.LatestEvidenceAt);
        var source = Assert.Single(competency.Sources);
        Assert.Equal("cv_analysis", source.SourceType);
        Assert.Equal(1, source.EvidenceCount);
        Assert.Equal(at, source.LatestEvidenceAt);
    }

    [Fact]
    public void MultipleScoresUseEqualWeightAndRoundOnceAwayFromZero()
    {
        var profile = SkillProfileAggregator.Aggregate([
            Evidence("answer:1", "interview.clarity", "Clarity", "interview", "interview", 80, DateTimeOffset.UtcNow),
            Evidence("answer:2", "interview.clarity", "Clarity", "interview", "interview", 81, DateTimeOffset.UtcNow.AddMinutes(1))
        ]);

        var competency = Assert.Single(profile.Competencies);
        Assert.Equal(81, competency.Score);
        Assert.Equal(2, competency.EvidenceCount);
        Assert.Equal(2, Assert.Single(competency.Sources).EvidenceCount);
    }

    [Fact]
    public void DuplicateEvidenceIdentityIsCountedOnceUsingTheLatestObservation()
    {
        var older = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(1);

        var profile = SkillProfileAggregator.Aggregate([
            Evidence("scenario-attempt:1", "scenario.problem_solving", "Problem Solving", "scenario", "scenario", 40, older),
            Evidence("scenario-attempt:1", "scenario.problem_solving", "Problem Solving", "scenario", "scenario", 90, newer),
            Evidence("scenario-attempt:2", "scenario.problem_solving", "Problem Solving", "scenario", "scenario", 80, older)
        ]);

        var competency = Assert.Single(profile.Competencies);
        Assert.Equal(85, competency.Score);
        Assert.Equal(2, competency.EvidenceCount);
        Assert.Equal(newer, competency.LatestEvidenceAt);
    }

    [Fact]
    public void InvalidScoresAndMissingNumericEvidenceAreIgnored()
    {
        var profile = SkillProfileAggregator.Aggregate([
            Evidence("invalid-low", "resume.structure", "Structure", "resume", "cv_analysis", -1, DateTimeOffset.UtcNow),
            Evidence("invalid-high", "resume.structure", "Structure", "resume", "cv_analysis", 101, DateTimeOffset.UtcNow),
            new SkillProfileEvidence("", "resume.structure", "Structure", "resume", "cv_analysis", 50, DateTimeOffset.UtcNow)
        ]);

        Assert.Empty(profile.Competencies);
    }

    [Fact]
    public void CompetenciesKeepDifferentMeasurementsAndUseDeterministicOrdering()
    {
        var profile = SkillProfileAggregator.Aggregate([
            Evidence("interview-clarity", "interview.clarity", "Clarity", "interview", "interview", 80, DateTimeOffset.UtcNow),
            Evidence("resume-clarity", "resume.clarity", "Clarity", "resume", "cv_analysis", 70, DateTimeOffset.UtcNow),
            Evidence("scenario-z", "scenario.zed", "Zed", "scenario", "scenario", 70, DateTimeOffset.UtcNow),
            Evidence("behavioral-a", "behavioral.action", "Action", "behavioral", "star_attempt", 75, DateTimeOffset.UtcNow)
        ]);

        Assert.Equal(["behavioral.action", "interview.clarity", "resume.clarity", "scenario.zed"],
            profile.Competencies.Select(item => item.Code));
        Assert.NotEqual(profile.Competencies.Single(item => item.Code == "resume.clarity").Code,
            profile.Competencies.Single(item => item.Code == "interview.clarity").Code);
    }

    [Fact]
    public void TaxonomyNormalizesCamelCaseWhitespaceAndSeparatorsDeterministically()
    {
        Assert.Equal("resume.technical_skill_match", SkillProfileTaxonomy.CreateCode(" Resume ", "technicalSkillMatch"));
        Assert.Equal("scenario.problem_solving", SkillProfileTaxonomy.CreateCode("scenario", "Problem-Solving"));
        Assert.Equal("Technical Skill Match", SkillProfileTaxonomy.DisplayName("technicalSkillMatch"));
    }

    [Fact]
    public void QualitativeSignalsAreDeduplicatedAndSortedWithoutNumericCompetencies()
    {
        var at = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var profile = SkillProfileAggregator.Aggregate([], [
            new SkillProfileWeaknessSignal("cv_analysis", " SQL ", at),
            new SkillProfileWeaknessSignal("cv_analysis", "sql", at.AddHours(1)),
            new SkillProfileWeaknessSignal("cv_analysis", "API design", at)
        ]);

        Assert.Empty(profile.Competencies);
        Assert.Equal(["API design", "sql"], profile.WeaknessSignals.Select(item => item.Label));
        Assert.Equal(at.AddHours(1), profile.WeaknessSignals.Single(item => item.Label == "sql").LatestEvidenceAt);
    }

    [Fact]
    public void EmptyInputReturnsAnEmptyProfile()
    {
        var profile = SkillProfileAggregator.Aggregate([]);

        Assert.Empty(profile.Competencies);
        Assert.Empty(profile.WeaknessSignals);
    }

    private static SkillProfileEvidence Evidence(
        string identity,
        string code,
        string name,
        string category,
        string sourceType,
        int score,
        DateTimeOffset at) => new(identity, code, name, category, sourceType, score, at);
}
