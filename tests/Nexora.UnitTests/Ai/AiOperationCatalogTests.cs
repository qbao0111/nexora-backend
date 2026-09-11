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
                CoachingTips: ["Keep going"]),
            ScoreScale: AiOperations.ScoreScale,
            Strengths: ["Clear explanation of the answer"],
            Improvements: ["Add one concrete example if available"],
            ImprovedAnswer: "Overall good answer with a clear explanation.");

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
            Star: null,
            ScoreScale: AiOperations.ScoreScale);

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
                CoachingTips: []),
            ScoreScale: AiOperations.ScoreScale);

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
                CoachingTips: ["Keep going"]),
            ScoreScale: AiOperations.ScoreScale,
            Strengths: ["Good answer"],
            Improvements: ["Add one concrete example if available"],
            ImprovedAnswer: "Overall good answer with clear evidence.");

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
            CoachingTips: [],
            ScoreScale: AiOperations.ScoreScale);

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
            CoachingTips: ["Improve result"],
            ScoreScale: AiOperations.ScoreScale);

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
    public void StarComponentValidatorTestADetectedFalseWithPositiveScoreFailsMismatch()
    {
        var component = new StarComponentEvaluation(60, false, "", "Some feedback");
        var result = StarComponentValidator.Validate(component, "action");

        Assert.False(result.IsValid);
        Assert.Equal("star.component_detected_score_mismatch", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void StarComponentValidatorTestBDetectedTrueWithBlankEvidenceFailsWithoutEvidence()
    {
        var component = new StarComponentEvaluation(80, true, "   ", "Some feedback");
        var result = StarComponentValidator.Validate(component, "action");

        Assert.False(result.IsValid);
        Assert.Equal("star.component_detected_without_evidence", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void StarComponentValidatorTestCDetectedFalseWithZeroScoreAndEmptyEvidenceIsValidAbsence()
    {
        var component = new StarComponentEvaluation(0, false, "", "Không có action");
        var result = StarComponentValidator.Validate(component, "action");

        Assert.True(result.IsValid);
        Assert.NotNull(result.NormalizedValue);
        Assert.False(result.NormalizedValue.Detected);
        Assert.Equal(0, result.NormalizedValue.Score);
        Assert.Equal(string.Empty, result.NormalizedValue.Evidence);
    }

    [Fact]
    public void StarComponentValidatorTestDDetectedTrueWithPositiveScoreAndEvidenceIsValid()
    {
        var component = new StarComponentEvaluation(90, true, "candidate evidence quote", "Good action");
        var result = StarComponentValidator.Validate(component, "action");

        Assert.True(result.IsValid);
        Assert.NotNull(result.NormalizedValue);
        Assert.True(result.NormalizedValue.Detected);
        Assert.Equal(90, result.NormalizedValue.Score);
        Assert.Equal("candidate evidence quote", result.NormalizedValue.Evidence);
    }

    [Fact]
    public void StarComponentValidatorTestENullComponentNormalizesGracefullyAsUndetected()
    {
        var result = StarComponentValidator.Validate(null, "task");

        Assert.True(result.IsValid);
        Assert.NotNull(result.NormalizedValue);
        Assert.False(result.NormalizedValue.Detected);
        Assert.Equal(0, result.NormalizedValue.Score);
        Assert.Equal(string.Empty, result.NormalizedValue.Evidence);
        Assert.Contains("task", result.NormalizedValue.Feedback);
    }

    [Fact]
    public void StarComponentValidatorTestFBlankFeedbackNormalizesGracefully()
    {
        var component = new StarComponentEvaluation(0, false, "", "   ");
        var result = StarComponentValidator.Validate(component, "situation");

        Assert.True(result.IsValid);
        Assert.NotNull(result.NormalizedValue);
        Assert.False(result.NormalizedValue.Detected);
        Assert.False(string.IsNullOrWhiteSpace(result.NormalizedValue.Feedback));
    }

    [Fact]
    public void PromptContractTestLInterviewEvaluateAndStarEvaluateShareCanonicalStarSemantics()
    {
        var interviewInstructions = AiOperations.InterviewEvaluate.Instructions;
        var starInstructions = AiOperations.StarEvaluate.Instructions;

        Assert.Contains(StarSemantics.CanonicalInstructions, interviewInstructions);
        Assert.Contains(StarSemantics.CanonicalInstructions, starInstructions);

        // Verify key technical action and result definitions are included
        Assert.Contains("EXPLAIN ANALYZE", interviewInstructions);
        Assert.Contains("pg_stat_statements", interviewInstructions);
        Assert.Contains("Redis", interviewInstructions);
        Assert.Contains("index", interviewInstructions);
        Assert.Contains("inspecting logs", interviewInstructions);
        Assert.Contains("coordinating with Tech Lead", interviewInstructions);
        Assert.Contains("coordinating with DBA", interviewInstructions);
        Assert.Contains("latency improved", interviewInstructions);
        Assert.Contains("system recovered", interviewInstructions);
    }

    [Fact]
    public void BuildRepairInstructionsAppendsSpecificFailureContext()
    {
        var validation = AiValidationResult<AnswerEvaluation>.Failure("rubric.criteria_missing", "semantic", repairable: true);
        var original = "Evaluate the interview answer.";

        var repairInstructions = AiOperations.InterviewEvaluate.BuildRepairInstructions(validation, original);

        Assert.Contains("IMPORTANT RUBRIC CORRECTION INSTRUCTION", repairInstructions);
        Assert.Contains("rubric.criteria_missing", repairInstructions);
        Assert.Contains("exactly four rubric items", repairInstructions);
        Assert.Contains(original, repairInstructions);
    }

    [Fact]
    public void BuildRepairInstructionsForStarAppendsStarCorrectionInstruction()
    {
        var validation = AiValidationResult<AnswerEvaluation>.Failure("star.component_detected_without_evidence", "semantic", repairable: true);
        var original = "Evaluate the interview answer.";

        var repairInstructions = AiOperations.InterviewEvaluate.BuildRepairInstructions(validation, original);

        Assert.Contains("IMPORTANT STAR CORRECTION INSTRUCTION", repairInstructions);
        Assert.Contains("star.component_detected_without_evidence", repairInstructions);
        Assert.Contains("question focus does not restrict STAR extraction", repairInstructions, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InterviewEvaluateNormalizesGroundedActionableCoaching()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "The API debugging approach is described."),
                new RubricScore("structure", 80, "The answer is ordered."),
                new RubricScore("completeness", 80, "The API issue is covered."),
                new RubricScore("clarity", 80, "The answer is clear.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["The API debugging is clear."],
            ["Add a concrete result if available."],
            "I debugged the API and can add a concrete result if available.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-valid", ExpectedStar: false, CandidateAnswer: "I debugged the API."));

        Assert.True(result.IsValid);
        Assert.Equal(["The API debugging is clear."], result.NormalizedValue!.Strengths);
        Assert.Equal(["Add a concrete result if available."], result.NormalizedValue.Improvements);
        Assert.Contains("debugged the API", result.NormalizedValue.ImprovedAnswer);
    }

    [Fact]
    public void InterviewEvaluateRejectsFabricatedImprovedAnswerFacts()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["The API explanation is clear."],
            ["Add a concrete result if available."],
            "I debugged the API and reduced latency by 99% using Kubernetes.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-fabricated", ExpectedStar: false, CandidateAnswer: "I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("interview.improved_answer_fabricated", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateRejectsFabricatedStrengthFacts()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["Clear response reduced latency by 99%."],
            ["Add a concrete result if available."],
            "I debugged the API and add a concrete result if available.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-strength-fabricated", ExpectedStar: false, CandidateAnswer: "I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("interview.strengths_ungrounded", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateRejectsGenericStrengthWithoutAnswerEvidence()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["Clear leadership under pressure."],
            ["Add one concrete result if available."],
            "I debugged the API and add a concrete result if available.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-generic-strength", ExpectedStar: false, CandidateAnswer: "I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("interview.strengths_ungrounded", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateRejectsNovelStrengthClaimWithAnswerOverlap()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["Strong API leadership under pressure."],
            ["Add one concrete result if available."],
            "I debugged the API.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-strength-claim", ExpectedStar: false, CandidateAnswer: "I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("interview.strengths_ungrounded", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateAcceptsFaithfulImprovedAnswerParaphrase()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["The API response is clear."],
            ["Add one concrete result if available."],
            "I resolved the API issue.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-paraphrase", ExpectedStar: false, CandidateAnswer: "I fixed the API bug."));

        Assert.True(result.IsValid, result.FailureReason);
        Assert.Equal("I resolved the API issue.", result.NormalizedValue!.ImprovedAnswer);
    }

    [Fact]
    public void InterviewEvaluateRejectsUnlistedAchievementAndTechnologyInImprovedAnswer()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["The API debugging is clear."],
            ["Add one concrete result if available."],
            "I debugged the API and mentored the team through a RabbitMQ migration.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-unlisted-facts", ExpectedStar: false, CandidateAnswer: "I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("interview.improved_answer_fabricated", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateRejectsNovelTechnologyIdentifierInImprovedAnswer()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["The API response is clear."],
            ["Add one concrete result if available."],
            "elasticsearch improved the API issue.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-open-technology", ExpectedStar: false, CandidateAnswer: "I debugged the API."));

        Assert.False(result.IsValid);
        Assert.Equal("interview.improved_answer_fabricated", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateAcceptsGroundedShortVietnameseAnswer()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Tốt.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            ["Phần trả lời phù hợp với vai trò."],
            ["Bổ sung một ví dụ cụ thể nếu có."],
            "Tôi phù hợp với vai trò.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-short-vietnamese", ExpectedStar: false, CandidateAnswer: "Tôi phù hợp với vai trò."));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Fact]
    public void InterviewEvaluateRejectsMissingCoachingFields()
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded."),
                new RubricScore("structure", 80, "Grounded."),
                new RubricScore("completeness", 80, "Grounded."),
                new RubricScore("clarity", 80, "Grounded.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            [],
            ["More detail"],
            "I debugged the API.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(raw, new AiOperationContext("coaching-missing", ExpectedStar: false));

        Assert.False(result.IsValid);
        Assert.Equal("interview.strengths_invalid", result.FailureReason);
        Assert.True(result.Repairable);
    }
}
