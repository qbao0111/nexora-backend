using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Ai;

public sealed class AiOperationCatalogTests
{
    [Fact]
    public void InterviewEvaluateNonBehavioralQuestionNormalizesApplicableTrueToFalseWithoutFailing()
    {
        var rawEvaluation = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Evidence A"),
                new RubricScore("structure", 80, "Evidence B"),
                new RubricScore("completeness", 80, "Evidence C"),
                new RubricScore("clarity", 80, "Evidence D")
            ],
            "Overall good answer",
            new StarEvaluation(
                Applicable: true,
                OverallScore: 80,
                Situation: new StarComponentEvaluation(80, true, "Sit evidence", "Sit fb"),
                Task: new StarComponentEvaluation(80, true, "Task evidence", "Task fb"),
                Action: new StarComponentEvaluation(80, true, "Act evidence", "Act fb"),
                Result: new StarComponentEvaluation(80, true, "Res evidence", "Res fb"),
                MissingElements: [],
                Strengths: ["Good"],
                CoachingTips: ["Keep going"]));

        // Non-behavioral question
        var context = new AiOperationContext("test-corr", ExpectedStar: false);

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(rawEvaluation, context);

        Assert.True(result.IsValid);
        Assert.NotNull(result.NormalizedValue);
        Assert.NotNull(result.NormalizedValue.Star);
        Assert.False(result.NormalizedValue.Star.Applicable);
        Assert.Null(result.NormalizedValue.Star.OverallScore);
        Assert.Null(result.NormalizedValue.Star.Situation);
        Assert.Null(result.NormalizedValue.Star.Task);
        Assert.Null(result.NormalizedValue.Star.Action);
        Assert.Null(result.NormalizedValue.Star.Result);
        Assert.Empty(result.NormalizedValue.Star.MissingElements);
    }

    [Fact]
    public void InterviewEvaluateBehavioralQuestionRejectsOmittedStarAsRepairable()
    {
        var rawEvaluation = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Evidence A"),
                new RubricScore("structure", 80, "Evidence B"),
                new RubricScore("completeness", 80, "Evidence C"),
                new RubricScore("clarity", 80, "Evidence D")
            ],
            "Overall good answer",
            Star: null);

        var context = new AiOperationContext("test-corr", ExpectedStar: true);

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(rawEvaluation, context);

        Assert.False(result.IsValid);
        Assert.Equal("star.missing", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateBehavioralQuestionRejectsApplicableFalseAsRepairable()
    {
        var rawEvaluation = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Evidence A"),
                new RubricScore("structure", 80, "Evidence B"),
                new RubricScore("completeness", 80, "Evidence C"),
                new RubricScore("clarity", 80, "Evidence D")
            ],
            "Overall good answer",
            new StarEvaluation(
                Applicable: false,
                OverallScore: null,
                Situation: null,
                Task: null,
                Action: null,
                Result: null,
                MissingElements: [],
                Strengths: [],
                CoachingTips: []));

        var context = new AiOperationContext("test-corr", ExpectedStar: true);

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(rawEvaluation, context);

        Assert.False(result.IsValid);
        Assert.Equal("star.applicability_mismatch", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateBehavioralQuestionComputesAuthoritativeStarWeightedScore()
    {
        var rawEvaluation = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Evidence A"),
                new RubricScore("structure", 80, "Evidence B"),
                new RubricScore("completeness", 80, "Evidence C"),
                new RubricScore("clarity", 80, "Evidence D")
            ],
            "Overall good answer",
            new StarEvaluation(
                Applicable: true,
                OverallScore: 999, // Intentional arbitrary score from AI
                Situation: new StarComponentEvaluation(100, true, "Sit evidence", "Sit fb"),
                Task: new StarComponentEvaluation(100, true, "Task evidence", "Task fb"),
                Action: new StarComponentEvaluation(50, true, "Act evidence", "Act fb"),
                Result: new StarComponentEvaluation(50, true, "Res evidence", "Res fb"),
                MissingElements: [],
                Strengths: ["Good"],
                CoachingTips: ["Keep going"]));

        var context = new AiOperationContext("test-corr", ExpectedStar: true);

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(rawEvaluation, context);

        Assert.True(result.IsValid);
        Assert.NotNull(result.NormalizedValue?.Star);
        // Weighted formula: sit * 0.20 + task * 0.20 + act * 0.35 + res * 0.25
        // 100*0.2 + 100*0.2 + 50*0.35 + 50*0.25 = 20 + 20 + 17.5 + 12.5 = 70
        Assert.Equal(70, result.NormalizedValue.Star.OverallScore);
        // Scores < 60 automatically added to missing elements
        Assert.Contains("action", result.NormalizedValue.Star.MissingElements);
        Assert.Contains("result", result.NormalizedValue.Star.MissingElements);
    }

    [Fact]
    public void StarEvaluateRejectsApplicableFalseAsRepairable()
    {
        var rawStar = new StarEvaluation(
            Applicable: false,
            OverallScore: null,
            Situation: null,
            Task: null,
            Action: null,
            Result: null,
            MissingElements: [],
            Strengths: [],
            CoachingTips: []);

        var context = new AiOperationContext("test-corr", ExpectedStar: true);

        var result = AiOperations.StarEvaluate.NormalizeAndValidate(rawStar, context);

        Assert.False(result.IsValid);
        Assert.Equal("star.applicability_mismatch", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void StarEvaluateCalculatesWeightedScoreAndNormalizes()
    {
        var rawStar = new StarEvaluation(
            Applicable: true,
            OverallScore: 0,
            Situation: new StarComponentEvaluation(80, true, "Sit evidence", "Sit fb"),
            Task: new StarComponentEvaluation(70, true, "Task evidence", "Task fb"),
            Action: new StarComponentEvaluation(60, true, "Act evidence", "Act fb"),
            Result: new StarComponentEvaluation(40, true, "Res evidence", "Res fb"),
            MissingElements: [],
            Strengths: ["Good"],
            CoachingTips: ["Improve result"]);

        var context = new AiOperationContext("test-corr", ExpectedStar: true);

        var result = AiOperations.StarEvaluate.NormalizeAndValidate(rawStar, context);

        Assert.True(result.IsValid);
        Assert.NotNull(result.NormalizedValue);
        Assert.True(result.NormalizedValue.Applicable);
        // 80*0.2 + 70*0.2 + 60*0.35 + 40*0.25 = 16 + 14 + 21 + 10 = 61
        Assert.Equal(61, result.NormalizedValue.OverallScore);
        Assert.Contains("result", result.NormalizedValue.MissingElements);
    }

    [Fact]
    public void BuildRepairInstructionsAppendsSpecificFailureContext()
    {
        var validation = AiValidationResult<AnswerEvaluation>.Failure("rubric.criteria_missing", "semantic", repairable: true);
        var original = "Evaluate the interview answer.";

        var repairInstructions = AiOperations.InterviewEvaluate.BuildRepairInstructions(validation, original);

        Assert.Contains("IMPORTANT CORRECTION INSTRUCTION", repairInstructions);
        Assert.Contains("rubric.criteria_missing", repairInstructions);
        Assert.Contains(original, repairInstructions);
    }
}
