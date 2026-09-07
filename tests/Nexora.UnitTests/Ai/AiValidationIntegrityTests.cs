using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Ai;

public sealed class AiValidationIntegrityTests
{
    private static readonly AiOperationContext Context = new("validation-test", ExpectedStar: true);

    [Fact]
    public void ResumeProfileAcceptsUsefulSkillsWithoutSummary()
    {
        var result = ResumeProfileValidator.NormalizeAndValidate(Profile(summary: null, skills: [" C# "]));

        Assert.True(result.IsValid);
        Assert.Equal(["C#"], result.NormalizedValue!.Skills);
    }

    [Fact]
    public void ResumeProfileAcceptsUsefulExperienceWithoutSummary()
    {
        var profile = Profile(summary: "", experiences: [new ResumeExperience("Acme", "Engineer", null, null, [])]);

        Assert.True(AiOperations.ResumeProfile.NormalizeAndValidate(profile, Context).IsValid);
    }

    [Fact]
    public void ResumeProfileRejectsCompletelyEmptyProfile()
    {
        var result = ResumeProfileValidator.NormalizeAndValidate(Profile());

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData("strengths")]
    [InlineData("gaps")]
    [InlineData("recommendations")]
    public void ResumeAnalysisRejectsMissingGroundedCollection(string missing)
    {
        var result = AiOperations.ResumeAnalysis.NormalizeAndValidate(new ResumeAnalysisOutput(
            missing == "strengths" ? [] : ["Grounded strength"],
            missing == "gaps" ? [] : ["Grounded gap"],
            missing == "recommendations" ? [] : ["Grounded recommendation"]), Context);

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
        Assert.Contains(missing, result.FailureReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "valid")]
    public void FirstQuestionValidatesWithoutTruncation(bool overlong, string value)
    {
        var content = overlong ? new string('x', 2_001) : value;
        var result = AiOperations.InterviewFirstQuestion.NormalizeAndValidate(new GeneratedQuestion(content), Context);

        Assert.Equal(!overlong && value.Length > 0, result.IsValid);
        if (!result.IsValid) Assert.True(result.Repairable);
    }

    [Fact]
    public void FollowupRejectsOverlongQuestionInsteadOfTruncating()
    {
        var result = AiOperations.InterviewFollowup.NormalizeAndValidate(new GeneratedQuestion(new string('x', 2_001)), Context);

        Assert.False(result.IsValid);
        Assert.Equal("question.too_long", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData("strengths")]
    [InlineData("gaps")]
    [InlineData("actionPlan")]
    public void InterviewReportRejectsMissingGroundedCollection(string missing)
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(new InterviewReportOutput(
            Rubric(),
            missing == "strengths" ? [] : ["Grounded strength"],
            missing == "gaps" ? [] : ["Grounded gap"],
            missing == "actionPlan" ? [] : ["Grounded action"]), Context);

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void ScenarioRejectsOutOfRangeOverallScore(int score)
    {
        var result = AiOperations.ScenarioEvaluate.NormalizeAndValidate(ValidScenario(overallScore: score), Context);

        Assert.False(result.IsValid);
        Assert.Equal("scenario.score_out_of_range", result.FailureReason);
    }

    [Fact]
    public void ScenarioRejectsTooFewDimensionsWithoutSynthesizingAny()
    {
        var result = AiOperations.ScenarioEvaluate.NormalizeAndValidate(ValidScenario(dimensions: []), Context);

        Assert.False(result.IsValid);
        Assert.Equal("scenario.dimensions_count_invalid", result.FailureReason);
        Assert.Null(result.NormalizedValue);
    }

    [Theory]
    [InlineData("criterion")]
    [InlineData("evidence")]
    [InlineData("feedback")]
    public void ScenarioRejectsMalformedDimension(string blankField)
    {
        var invalid = new ScenarioDimensionEvaluation(
            blankField == "criterion" ? " " : "analysis",
            75,
            blankField == "evidence" ? " " : "Candidate evidence",
            blankField == "feedback" ? " " : "Grounded feedback");
        var result = AiOperations.ScenarioEvaluate.NormalizeAndValidate(
            ValidScenario(dimensions: [invalid, Dimension("communication")]), Context);

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData("strengths")]
    [InlineData("gaps")]
    [InlineData("approach")]
    public void ScenarioRejectsMissingRequiredEvaluationCollection(string missing)
    {
        var result = AiOperations.ScenarioEvaluate.NormalizeAndValidate(new ScenarioEvaluationResult(
            75,
            [Dimension("analysis"), Dimension("communication")],
            missing == "strengths" ? [] : ["Grounded strength"],
            missing == "gaps" ? [] : ["Grounded gap"],
            missing == "approach" ? [] : ["Grounded approach"],
            "Grounded feedback"), Context);

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData("strengths")]
    [InlineData("coachingTips")]
    public void StandaloneStarRejectsMissingRequiredCoachingCollection(string missing)
    {
        var star = new StarEvaluation(true, 50,
            new StarComponentEvaluation(70, true, "s", "feedback"),
            new StarComponentEvaluation(70, true, "t", "feedback"),
            new StarComponentEvaluation(70, true, "a", "feedback"),
            new StarComponentEvaluation(70, true, "r", "feedback"),
            [],
            missing == "strengths" ? [] : ["Grounded strength"],
            missing == "coachingTips" ? [] : ["Grounded tip"]);

        var result = AiOperations.StarEvaluate.NormalizeAndValidate(star, Context);

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
    }

    private static ResumeProfile Profile(
        string? summary = null,
        IReadOnlyCollection<string>? skills = null,
        IReadOnlyCollection<ResumeExperience>? experiences = null) =>
        new(summary, skills ?? [], experiences ?? [], [], [], [], []);

    private static IReadOnlyCollection<RubricScore> Rubric() =>
    [
        new("correctness", 75, "Evidence"),
        new("structure", 75, "Evidence"),
        new("completeness", 75, "Evidence"),
        new("clarity", 75, "Evidence")
    ];

    private static ScenarioDimensionEvaluation Dimension(string criterion) =>
        new(criterion, 75, "Candidate evidence", "Grounded feedback");

    private static ScenarioEvaluationResult ValidScenario(
        int overallScore = 75,
        IReadOnlyCollection<ScenarioDimensionEvaluation>? dimensions = null) =>
        new(overallScore, dimensions ?? [Dimension("analysis"), Dimension("communication")],
            ["Grounded strength"], ["Grounded gap"], ["Grounded approach"], "Grounded feedback");
}
