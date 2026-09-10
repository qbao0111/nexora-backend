using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Ai;

public sealed class ResumeAnalysisV2Tests
{
    private static readonly AiOperationContext JobContext = new(
        "job-analysis",
        Metadata: new Dictionary<string, string>
        {
            [ResumeAnalysisMetadata.Mode] = ResumeAnalysisModes.JobTargeted
        });

    private static readonly AiOperationContext FieldContext = new(
        "field-analysis",
        Metadata: new Dictionary<string, string>
        {
            [ResumeAnalysisMetadata.Mode] = ResumeAnalysisModes.FieldBenchmark
        });

    [Fact]
    public void JobTargetedRequiresItsExplicitModeAndFullShape()
    {
        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(JobOutput(), JobContext);

        Assert.True(result.IsValid);
        Assert.Equal(78, result.NormalizedValue!.MatchScore);
        Assert.Null(result.NormalizedValue.ReadinessScore);
        Assert.Equal(ResumeAnalysisModes.JobTargeted, result.NormalizedValue.Mode);
        Assert.Equal(5, result.NormalizedValue.Breakdown!.Count);
    }

    [Fact]
    public void FieldBenchmarkUsesReadinessScoreAndItsOwnBreakdown()
    {
        var result = AiOperations.ResumeAnalysisFieldBenchmark.NormalizeAndValidate(FieldOutput(), FieldContext);

        Assert.True(result.IsValid);
        Assert.Equal(74, result.NormalizedValue!.ReadinessScore);
        Assert.Null(result.NormalizedValue.MatchScore);
        Assert.Equal(ResumeAnalysisModes.FieldBenchmark, result.NormalizedValue.Mode);
        Assert.Equal(6, result.NormalizedValue.Breakdown!.Count);
    }

    [Fact]
    public void CrossModeOutputIsRejectedInsteadOfBeingInterpretedByJobTargetedOperation()
    {
        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(FieldOutput(), JobContext);

        Assert.False(result.IsValid);
        Assert.Equal("resume.analysis_mode_invalid", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void OperationContextModeMustMatchTheSelectedOperation()
    {
        var result = AiOperations.ResumeAnalysisFieldBenchmark.NormalizeAndValidate(FieldOutput(), JobContext);

        Assert.False(result.IsValid);
        Assert.Equal("resume.analysis_mode_invalid", result.FailureReason);
    }

    [Fact]
    public void FieldBenchmarkRejectsMissingContextSpecificInputThroughOperationMetadata()
    {
        var result = AiOperations.ResumeAnalysisFieldBenchmark.NormalizeAndValidate(
            FieldOutput() with { Mode = null },
            FieldContext);

        Assert.False(result.IsValid);
        Assert.Equal("resume.analysis_mode_invalid", result.FailureReason);
    }

    [Fact]
    public void StrictJobTargetedValidationRejectsMissingBreakdownDimension()
    {
        var output = JobOutput() with
        {
            Breakdown = new Dictionary<string, int>
            {
                ["technicalSkillMatch"] = 80,
                ["experienceRelevance"] = 78,
                ["impactEvidence"] = 70,
                ["clarity"] = 82
            }
        };

        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(output, JobContext);

        Assert.False(result.IsValid);
        Assert.Equal("resume.breakdown_invalid", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void StrictValidationRejectsOverflowInsteadOfSilentlyTruncatingCollections()
    {
        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(
            JobOutput() with { Strengths = Enumerable.Repeat("grounded", 7).ToArray() },
            JobContext);

        Assert.False(result.IsValid);
        Assert.Equal("resume.strengths_blank", result.FailureReason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void JobTargetedRejectsScoresOutsideTheCanonicalRange(int score)
    {
        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(
            JobOutput() with { MatchScore = score },
            JobContext);

        Assert.False(result.IsValid);
        Assert.Equal("resume.match_score_invalid", result.FailureReason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void FieldBenchmarkRejectsScoresOutsideTheCanonicalRange(int score)
    {
        var result = AiOperations.ResumeAnalysisFieldBenchmark.NormalizeAndValidate(
            FieldOutput() with { ReadinessScore = score },
            FieldContext);

        Assert.False(result.IsValid);
        Assert.Equal("resume.readiness_score_invalid", result.FailureReason);
    }

    [Fact]
    public void StrictValidationRejectsBlankItemsInsteadOfSilentlyDroppingThem()
    {
        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(
            JobOutput() with { Gaps = ["Grounded gap", " "] },
            JobContext);

        Assert.False(result.IsValid);
        Assert.Equal("resume.gaps_blank", result.FailureReason);
    }

    [Fact]
    public void MissingModeIsRejectedInsteadOfUsingAlegacyContract()
    {
        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(
            new ResumeAnalysisOutput(["Strength"], ["Gap"], ["Recommendation"]),
            new AiOperationContext("legacy"));

        Assert.False(result.IsValid);
        Assert.Equal("resume.analysis_mode_required", result.FailureReason);
    }

    [Fact]
    public void JobTargetedAllowsEmptyMatchedAndMissingCollections()
    {
        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(
            JobOutput() with { MatchedKeywordsOrSkills = [], MissingKeywordsOrSkills = [] },
            JobContext);

        Assert.True(result.IsValid);
        Assert.Empty(result.NormalizedValue!.MatchedKeywordsOrSkills!);
        Assert.Empty(result.NormalizedValue.MissingKeywordsOrSkills!);
    }

    private static ResumeAnalysisOutput JobOutput() => new(
        ["Relevant C# experience"],
        ["Limited distributed systems exposure"],
        ["Build a distributed systems project"],
        MatchScore: 78,
        Summary: "The profile has a grounded fit for the target role.",
        MatchedKeywordsOrSkills: ["C#", "PostgreSQL"],
        MissingKeywordsOrSkills: ["Distributed systems"],
        SectionFeedback: ["Experience is relevant."],
        Breakdown: new Dictionary<string, int>
        {
            ["technicalSkillMatch"] = 80,
            ["experienceRelevance"] = 78,
            ["impactEvidence"] = 70,
            ["clarity"] = 82,
            ["structure"] = 80
        },
        Mode: ResumeAnalysisModes.JobTargeted);

    private static ResumeAnalysisOutput FieldOutput() => new(
        ["Strong foundation"],
        ["Limited architecture ownership evidence"],
        ["Add a measurable architecture project"],
        ReadinessScore: 74,
        Summary: "The profile has a solid foundation for the target field.",
        SectionFeedback: ["Projects show relevant practice."],
        Breakdown: new Dictionary<string, int>
        {
            ["technicalFoundation"] = 82,
            ["projectEvidence"] = 72,
            ["experiencePresentation"] = 70,
            ["impactAchievements"] = 65,
            ["clarity"] = 80,
            ["roleAlignment"] = 76
        },
        Mode: ResumeAnalysisModes.FieldBenchmark);
}
