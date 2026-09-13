using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Recommendations;
using Nexora.Business.Skills;

namespace Nexora.UnitTests.Recommendations;

public sealed class NextPracticeRecommendationPolicyTests
{
    private static readonly DateTimeOffset OldEvidence = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NewEvidence = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void LowerPriorityNumberWins()
    {
        var result = Select(
            Activity("resume.clarity", "Clarity", priority: 2, id: 2),
            Activity("interview.structure", "Structure", priority: 1, id: 1),
            Competency("resume.clarity", "Clarity", 40, 1, OldEvidence),
            Competency("interview.structure", "Structure", 40, 1, OldEvidence));

        Assert.Equal(LearningPathValues.ResumeImprovement, result!.ActivityType);
        Assert.Equal(1, result.Priority);
    }

    [Fact]
    public void SamePriorityUsesStrongerEvidenceFirst()
    {
        var result = Select(
            Activity("resume.clarity", "Clarity", priority: 1, id: 2),
            Activity("resume.structure", "Structure", priority: 1, id: 1),
            Competency("resume.clarity", "Clarity", 40, 1, OldEvidence),
            Competency("resume.structure", "Structure", 40, 3, OldEvidence));

        Assert.Contains("Structure", result!.Reason, StringComparison.Ordinal);
        Assert.Contains("3 evidence items", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SamePriorityAndEvidenceUsesOlderPracticeFirst()
    {
        var result = Select(
            Activity("resume.clarity", "Clarity", priority: 1, id: 2),
            Activity("resume.structure", "Structure", priority: 1, id: 1),
            Competency("resume.clarity", "Clarity", 40, 2, NewEvidence),
            Competency("resume.structure", "Structure", 40, 2, OldEvidence));

        Assert.Contains("Structure", result!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedLearningPathPracticeCanMakeACompetencyMoreRecent()
    {
        var result = NextPracticeRecommendationPolicy.Select(
            Path(
                Activity("resume.clarity", "Completed Clarity", priority: 1, id: 1, status: LearningPathValues.Completed),
                Activity("resume.clarity", "Clarity", priority: 1, id: 2),
                Activity("resume.structure", "Structure", priority: 1, id: 3)),
            new SkillProfileView(
                [
                    Competency("resume.clarity", "Clarity", 40, 2, OldEvidence),
                    Competency("resume.structure", "Structure", 40, 2, new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero))
                ],
                []));

        Assert.Contains("Structure", result!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SamePriorityEvidenceAndRecencyUsesWeakerScoreFirst()
    {
        var result = Select(
            Activity("resume.clarity", "Clarity", priority: 1, id: 2),
            Activity("resume.structure", "Structure", priority: 1, id: 1),
            Competency("resume.clarity", "Clarity", 50, 2, OldEvidence),
            Competency("resume.structure", "Structure", 30, 2, OldEvidence));

        Assert.Contains("Structure", result!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void LearningPathOrderAndIdBreakTiesDeterministically()
    {
        var first = Activity("resume.first", "First", priority: 1, id: 2, sortOrder: 0);
        var second = Activity("resume.second", "Second", priority: 1, id: 1, sortOrder: 1);
        var profile = new SkillProfileView(
            [Competency("resume.first", "First", 40, 1, OldEvidence), Competency("resume.second", "Second", 40, 1, OldEvidence)],
            []);
        var path = Path(first, second);

        var firstSelection = NextPracticeRecommendationPolicy.Select(path, profile);
        var secondSelection = NextPracticeRecommendationPolicy.Select(path, profile);

        Assert.Equal(firstSelection, secondSelection);
        Assert.Contains("First", firstSelection!.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletedObsoleteAndStaleActivitiesAreExcluded()
    {
        var completed = Activity("resume.completed", "Completed", priority: 1, id: 1, status: LearningPathValues.Completed);
        var obsolete = Activity("resume.obsolete", "Obsolete", priority: 1, id: 2, status: LearningPathValues.Obsolete);
        var stale = Activity("resume.stale", "Stale", priority: 1, id: 3);
        var result = NextPracticeRecommendationPolicy.Select(
            Path(completed, obsolete, stale),
            new SkillProfileView([Competency("resume.stale", "Stale", 80, 4, NewEvidence)], []));

        Assert.Null(result);
    }

    [Fact]
    public void QualitativeFallbackDoesNotInventNumericEvidenceOrScore()
    {
        var result = NextPracticeRecommendationPolicy.Select(
            Path(Activity(null, "Missing SQL evidence", priority: 3, id: 1, type: LearningPathValues.ResumeImprovement)),
            new SkillProfileView([], [new SkillProfileWeaknessSignal("cv_analysis", "Missing SQL evidence", OldEvidence)]));

        Assert.NotNull(result);
        Assert.Equal(LearningPathValues.ResumeImprovement, result.ActivityType);
        Assert.Equal(3, result.Priority);
        Assert.Null(result.ResourceId);
        Assert.Equal(
            "Address this resume improvement next because no higher-priority evidence-backed practice activity is currently pending.",
            result.Reason);
    }

    [Fact]
    public void InterviewRecommendationIncludesPracticeAgainActionMetadata()
    {
        var sourceInterviewId = Guid.Parse("70000000-0000-0000-0000-000000000099");
        var result = NextPracticeRecommendationPolicy.Select(
            Path(Activity(
                "interview.correctness",
                "Correctness",
                priority: 1,
                id: 1,
                type: LearningPathValues.Interview,
                resourceId: sourceInterviewId)),
            new SkillProfileView([Competency("interview.correctness", "Correctness", 40, 1, OldEvidence)], []));

        Assert.Equal(LearningPathValues.Interview, result!.ActivityType);
        Assert.NotNull(result.Action);
        Assert.Equal("practice_again", result.Action!.Type);
        Assert.Equal(InterviewPracticeValues.Recommendation, result.Action.Reason);
        Assert.Equal(sourceInterviewId, result.Action.SourceInterviewId);
        Assert.Equal("correctness", result.Action.FocusTopic);
    }

    [Theory]
    [InlineData(LearningPathValues.Scenario, 20)]
    [InlineData(LearningPathValues.StarDrill, 15)]
    [InlineData(LearningPathValues.Interview, 20)]
    [InlineData(LearningPathValues.ResumeImprovement, 15)]
    [InlineData(LearningPathValues.ExternalLearning, 20)]
    public void EstimatedMinutesUseTheServerOwnedMapping(string type, int expectedMinutes)
    {
        Assert.True(NextPracticeDurationPolicy.TryGetEstimatedMinutes(type, out var minutes));
        Assert.Equal(expectedMinutes, minutes);
    }

    private static NextPracticeRecommendationView? Select(
        LearningPathActivityView first,
        LearningPathActivityView second,
        params SkillProfileCompetency[] competencies) =>
        NextPracticeRecommendationPolicy.Select(
            Path(first, second),
            new SkillProfileView(competencies, []));

    private static LearningPathView Path(params LearningPathActivityView[] activities) =>
        new(
            Guid.Parse("70000000-0000-0000-0000-000000000001"),
            Guid.Parse("70000000-0000-0000-0000-000000000002"),
            LearningPathValues.Active,
            OldEvidence,
            NewEvidence,
            new LearningPathProgressView(0, activities.Length, 0),
            [new LearningPathMilestoneView(
                Guid.Parse("70000000-0000-0000-0000-000000000003"),
                LearningPathValues.CriticalMilestone,
                "Critical gaps",
                0,
                LearningPathValues.Active,
                activities)]);

    private static LearningPathActivityView Activity(
        string? competencyCode,
        string title,
        int priority,
        int id,
        int sortOrder = 0,
        string status = LearningPathValues.Pending,
        string type = LearningPathValues.ResumeImprovement,
        Guid? resourceId = null) =>
        new(
            Guid.Parse($"70000000-0000-0000-0000-{id:000000000012}"),
            type,
            title,
            title,
            competencyCode,
            resourceId,
            null,
            priority,
            status,
            sortOrder,
            status == LearningPathValues.Completed ? NewEvidence : null,
            OldEvidence,
            NewEvidence);

    private static SkillProfileCompetency Competency(
        string code,
        string name,
        int score,
        int evidenceCount,
        DateTimeOffset latestEvidenceAt) =>
        new(code, name, code[..code.IndexOf('.')], score, evidenceCount, latestEvidenceAt, []);
}
