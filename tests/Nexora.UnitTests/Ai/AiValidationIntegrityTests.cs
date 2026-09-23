using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Ai;

public sealed class AiValidationIntegrityTests
{
    private static readonly AiOperationContext Context = new(
        "validation-test",
        ExpectedStar: true,
        Metadata: new Dictionary<string, string>
        {
            [ResumeAnalysisMetadata.Mode] = ResumeAnalysisModes.JobTargeted
        },
        GroundingTranscript: "Evidence Strength Gap Action Grounded answer");

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
            missing == "recommendations" ? [] : ["Grounded recommendation"],
            MatchScore: 70,
            Summary: "Grounded summary",
            MatchedKeywordsOrSkills: [],
            MissingKeywordsOrSkills: [],
            SectionFeedback: ["Grounded section feedback."],
            Breakdown: new Dictionary<string, int>
            {
                ["technicalSkillMatch"] = 70,
                ["experienceRelevance"] = 70,
                ["impactEvidence"] = 70,
                ["clarity"] = 70,
                ["structure"] = 70
            },
            Mode: ResumeAnalysisModes.JobTargeted), Context);

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
    [InlineData("gaps")]
    [InlineData("actionPlan")]
    public void InterviewReportRejectsMissingGroundedCollection(string missing)
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(new InterviewReportOutput(
            Rubric(),
            missing == "strengths" ? [] : ["Grounded strength"],
            missing == "gaps" ? [] : ["Grounded gap"],
            missing == "actionPlan" ? [] : ["Grounded action"],
            AiOperations.ScoreScale), Context);

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewReportRejectsMoreThanThreeGroundedItems()
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(new InterviewReportOutput(
            Rubric(),
            ["one", "two", "three", "four"],
            ["Grounded gap"],
            ["Grounded action"],
            AiOperations.ScoreScale), Context);

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewReportUsesExecutorOwnedRetryBudget()
    {
        Assert.Equal(2, AiOperations.InterviewReport.MaxAttempts);
    }

    [Fact]
    public void InterviewReportAllowsNoUnsupportedStrengths()
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(
            Report("I debugged the API.") with { Strengths = [] },
            ReportContext("I debugged the API."));

        Assert.True(result.IsValid, result.FailureReason);
        Assert.Empty(result.NormalizedValue!.Strengths);
    }

    [Fact]
    public void InterviewReportRejectsNovelNumericMetricInRubricEvidence()
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(
            Report("I improved latency by 70 percent using caching."),
            ReportContext("I improved latency by 20 percent using caching."));

        Assert.False(result.IsValid);
        Assert.Equal("report.rubric_evidence_ungrounded", result.FailureReason);
    }

    [Fact]
    public void InterviewReportRejectsNovelTechnologyInRubricEvidence()
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(
            Report("I migrated Kubernetes workloads."),
            ReportContext("I improved latency by 20 percent using caching."));

        Assert.False(result.IsValid);
        Assert.Equal("report.rubric_evidence_ungrounded", result.FailureReason);
    }

    [Fact]
    public void InterviewReportRejectsFabricatedStrength()
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(
            Report("I improved latency by 20 percent using caching.", "I led a team of ten engineers."),
            ReportContext("I improved latency by 20 percent using caching."));

        Assert.False(result.IsValid);
        Assert.Equal("report.strengths_ungrounded", result.FailureReason);
    }

    [Fact]
    public void InterviewReportAcceptsEvidenceAndStrengthGroundedInAnsweredContent()
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(
            Report("I improved latency by 20 percent using caching.", "I improved latency using caching."),
            ReportContext("I improved latency by 20 percent using caching."));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Fact]
    public void ResumeProfileOnlyFactDoesNotGroundInterviewReportEvidence()
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(
            Report("I built React applications."),
            ReportContext("I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("report.rubric_evidence_ungrounded", result.FailureReason);
    }

    [Fact]
    public void UnansweredQuestionTextDoesNotGroundInterviewReportEvidence()
    {
        var result = AiOperations.InterviewReport.NormalizeAndValidate(
            Report("I designed Kubernetes orchestration."),
            ReportContext("I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("report.rubric_evidence_ungrounded", result.FailureReason);
    }

    [Fact]
    public void InterviewReportStillRequiresCanonicalFourRubricCriteria()
    {
        var scores = new[]
        {
            new RubricScore("correctness", 80, "I debugged the API."),
            new RubricScore("structure", 80, "I debugged the API."),
            new RubricScore("completeness", 80, "I debugged the API."),
            new RubricScore("unsupported", 80, "I debugged the API.")
        };
        var result = AiOperations.InterviewReport.NormalizeAndValidate(
            new InterviewReportOutput(scores, ["I debugged the API."], ["Add evidence."], ["Add evidence."], AiOperations.ScoreScale),
            ReportContext("I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("rubric.criteria_extra", result.FailureReason);
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
            "Grounded feedback",
            AiOperations.ScoreScale), Context);

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
            missing == "coachingTips" ? [] : ["Grounded tip"],
            AiOperations.ScoreScale);

        var result = AiOperations.StarEvaluate.NormalizeAndValidate(star, Context);

        Assert.False(result.IsValid);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData("interview.evaluate", "0-100", true)]
    [InlineData("interview.evaluate", null, false)]
    [InlineData("interview.evaluate", "1-5", false)]
    [InlineData("interview.evaluate", "0-100 ", false)]
    [InlineData("interview.report", "0-100", true)]
    [InlineData("interview.report", null, false)]
    [InlineData("interview.report", "1-5", false)]
    [InlineData("interview.report", "0-100 ", false)]
    [InlineData("scenario.evaluate", "0-100", true)]
    [InlineData("scenario.evaluate", null, false)]
    [InlineData("scenario.evaluate", "1-5", false)]
    [InlineData("scenario.evaluate", "0-100 ", false)]
    [InlineData("star.evaluate", "0-100", true)]
    [InlineData("star.evaluate", null, false)]
    [InlineData("star.evaluate", "1-5", false)]
    [InlineData("star.evaluate", "0-100 ", false)]
    public void ScoringOperationsRequireExactScoreScale(string purpose, string? scoreScale, bool expectedValid)
    {
        var result = ValidateScoringOperation(purpose, scoreScale);

        Assert.Equal(expectedValid, result.IsValid);
        if (expectedValid)
        {
            Assert.Equal(AiOperations.ScoreScale, result.NormalizedScoreScale);
        }
        else
        {
            Assert.Equal("score.scale_invalid", result.FailureReason);
            Assert.True(result.Repairable);
        }
    }

    [Theory]
    [InlineData("interview.evaluate")]
    [InlineData("interview.report")]
    [InlineData("scenario.evaluate")]
    [InlineData("star.evaluate")]
    public void ScoringOperationSchemaRequiresScoreScaleAtRoot(string purpose)
    {
        var schema = purpose switch
        {
            AiPurposes.InterviewEvaluate => AiOperations.InterviewEvaluate.OutputSchema,
            AiPurposes.InterviewReport => AiOperations.InterviewReport.OutputSchema,
            AiPurposes.ScenarioEvaluate => AiOperations.ScenarioEvaluate.OutputSchema,
            AiPurposes.StarEvaluate => AiOperations.StarEvaluate.OutputSchema,
            _ => throw new ArgumentOutOfRangeException(nameof(purpose))
        };

        var required = schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("scoreScale", required);
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

    private static InterviewReportOutput Report(string evidence, string strength = "I improved latency by 20 percent using caching.") =>
        new(
            [
                new RubricScore("correctness", 80, evidence),
                new RubricScore("structure", 80, evidence),
                new RubricScore("completeness", 80, evidence),
                new RubricScore("clarity", 80, evidence)
            ],
            [strength],
            ["Add missing evidence if available."],
            ["Describe the result with concrete evidence if available."],
            AiOperations.ScoreScale);

    private static AiOperationContext ReportContext(string transcript) =>
        new("report-grounding", GroundingTranscript: transcript);

    private static ScenarioDimensionEvaluation Dimension(string criterion) =>
        new(criterion, 75, "Candidate evidence", "Grounded feedback");

    private static ScenarioEvaluationResult ValidScenario(
        int overallScore = 75,
        IReadOnlyCollection<ScenarioDimensionEvaluation>? dimensions = null) =>
        new(overallScore, dimensions ?? [Dimension("analysis"), Dimension("communication")],
            ["Grounded strength"], ["Grounded gap"], ["Grounded approach"], "Grounded feedback", AiOperations.ScoreScale);

    private static (bool IsValid, string? FailureReason, bool Repairable, string? NormalizedScoreScale) ValidateScoringOperation(
        string purpose,
        string? scoreScale)
    {
        if (purpose == AiPurposes.InterviewEvaluate)
        {
            var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
                new AnswerEvaluation(
                    Rubric(),
                    "Grounded feedback",
                    null,
                    scoreScale,
                    ["Grounded answer"],
                    ["Add one concrete example if available"],
                    "Grounded answer with concrete evidence."),
                new AiOperationContext("scale-test", ExpectedStar: false));
            return (result.IsValid, result.FailureReason, result.Repairable, result.NormalizedValue?.ScoreScale);
        }

        if (purpose == AiPurposes.InterviewReport)
        {
            var result = AiOperations.InterviewReport.NormalizeAndValidate(
                new InterviewReportOutput(Rubric(), ["Strength"], ["Gap"], ["Action"], scoreScale), Context);
            return (result.IsValid, result.FailureReason, result.Repairable, result.NormalizedValue?.ScoreScale);
        }

        if (purpose == AiPurposes.ScenarioEvaluate)
        {
            var scenario = ValidScenario() with { ScoreScale = scoreScale };
            var result = AiOperations.ScenarioEvaluate.NormalizeAndValidate(scenario, Context);
            return (result.IsValid, result.FailureReason, result.Repairable, result.NormalizedValue?.ScoreScale);
        }

        var star = new StarEvaluation(
            true,
            75,
            new StarComponentEvaluation(75, true, "s", "feedback"),
            new StarComponentEvaluation(75, true, "t", "feedback"),
            new StarComponentEvaluation(75, true, "a", "feedback"),
            new StarComponentEvaluation(75, true, "r", "feedback"),
            [],
            ["Strength"],
            ["Tip"],
            scoreScale);
        var starResult = AiOperations.StarEvaluate.NormalizeAndValidate(star, Context);
        return (starResult.IsValid, starResult.FailureReason, starResult.Repairable, starResult.NormalizedValue?.ScoreScale);
    }
}
