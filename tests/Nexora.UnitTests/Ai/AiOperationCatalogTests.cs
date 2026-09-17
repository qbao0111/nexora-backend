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
    public void BuildRepairInstructionsForInvalidImprovementsStatesTheExactContract()
    {
        var validation = AiValidationResult<AnswerEvaluation>.Failure("interview.improvements_invalid", "semantic", repairable: true);

        var repairInstructions = AiOperations.InterviewEvaluate.BuildRepairInstructions(validation, "Evaluate the interview answer.");

        Assert.Contains("JSON array", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("1 to 3 unique, non-empty strings", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("no longer than 500 characters", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("concrete action", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("must not assert or fabricate", repairInstructions, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRepairInstructionsForUngroundedImprovedAnswerExcludesEveryNonCandidateSource()
    {
        var validation = AiValidationResult<AnswerEvaluation>.Failure("interview.improved_answer_ungrounded", "semantic", repairable: true);

        var repairInstructions = AiOperations.InterviewEvaluate.BuildRepairInstructions(validation, "Evaluate the interview answer.");

        Assert.Contains("ONLY facts explicitly present in the ORIGINAL candidate answer", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("question, rubric, Job Description, resume context, Career Goal", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("new technologies, projects, responsibilities, metrics, team size, production claims", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("inferred experience", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("preserve the candidate's original answer", repairInstructions, StringComparison.Ordinal);
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
    public void InterviewEvaluateNormalizesWhitespaceAndEquivalentDuplicateImprovements()
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
            [" Add one concrete result. ", "add one concrete result.", " Explain the debugging sequence. ", "explain the debugging sequence."],
            "I debugged the API.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-normalized", ExpectedStar: false, CandidateAnswer: "I debugged the API."));

        Assert.True(result.IsValid, result.FailureReason);
        Assert.Equal(["Add one concrete result.", "Explain the debugging sequence."], result.NormalizedValue!.Improvements);
    }

    [Theory]
    [InlineData("khom")]
    [InlineData("không biết")]
    [InlineData("em chưa rõ")]
    [InlineData("idk")]
    [InlineData("I don't know")]
    [InlineData("banana weather football")]
    [InlineData("yes yes yes yes yes")]
    [InlineData("khom biết maybe API gì đó ???")]
    [InlineData("x")]
    public void InterviewEvaluateAllowsEmptyStrengthsWhenNoPositiveEvidenceIsGrounded(string candidateAnswer)
    {
        var raw = new AnswerEvaluation(
            [
                new RubricScore("correctness", 20, "No positive evidence is demonstrated in the answer."),
                new RubricScore("structure", 10, "No answer structure is demonstrated."),
                new RubricScore("completeness", 10, "The answer provides no supporting detail."),
                new RubricScore("clarity", 20, "The answer is too limited to demonstrate clarity.")
            ],
            "No positive evidence was demonstrated.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            [],
            ["Add one concrete example from your experience."],
            "Keep the same answer and add concrete evidence if available.");

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            raw,
            new AiOperationContext("coaching-no-positive-evidence", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(result.IsValid, result.FailureReason);
        Assert.NotNull(result.NormalizedValue);
        var normalized = result.NormalizedValue!;
        Assert.NotNull(normalized.Strengths);
        Assert.Empty(normalized.Strengths!);
        Assert.All(normalized.Scores, score => Assert.InRange(score.Score, 0, 59));
        Assert.NotNull(normalized.Improvements);
        Assert.Contains("Add", Assert.Single(normalized.Improvements!), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildRepairInstructionsForUngroundedStrengthsAllowsExplicitEmptyCollection()
    {
        var validation = AiValidationResult<AnswerEvaluation>.Failure("interview.strengths_ungrounded", "semantic", repairable: true);

        var repairInstructions = AiOperations.InterviewEvaluate.BuildRepairInstructions(validation, "Evaluate the interview answer.");

        Assert.Contains("\"strengths\": []", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("below 60", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("explicitly present in the ORIGINAL candidate answer", repairInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("validated rubric evidence", repairInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Do not invent", repairInstructions, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("Bạn đã nêu rõ việc sử dụng ASP.NET Core và PostgreSQL trong phần việc backend.")]
    [InlineData("Bạn mô tả cụ thể công nghệ ASP.NET Core và PostgreSQL đã sử dụng.")]
    public void InterviewEvaluateAcceptsVietnameseGroundedStrengthParaphrases(string strength)
    {
        const string candidateAnswer = "Tôi đã xây API bằng ASP.NET Core và sử dụng PostgreSQL để lưu dữ liệu.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(
                candidateAnswer,
                [strength],
                ["Bổ sung một kết quả cụ thể nếu có."],
                [
                    new RubricScore("correctness", 80, "The answer identifies ASP.NET Core and PostgreSQL."),
                    new RubricScore("structure", 80, "The answer describes the API work."),
                    new RubricScore("completeness", 80, "The answer includes the data store."),
                    new RubricScore("clarity", 80, "The technologies are named clearly.")
                ]),
            new AiOperationContext("coaching-vietnamese-strength", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Theory]
    [InlineData("Bạn thể hiện khả năng phân tích rõ ràng khi kiểm tra log và xác định deadlock.")]
    [InlineData("Bạn trình bày khá rõ cách xác định nguyên nhân deadlock.")]
    [InlineData("Bạn cho thấy tư duy xử lý có cấu trúc khi bắt đầu từ việc kiểm tra log.")]
    public void InterviewEvaluateAcceptsGroundedEvaluativeVietnameseStrengthParaphrases(string strength)
    {
        const string candidateAnswer = "Tôi kiểm tra log và xác định nguyên nhân deadlock.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, [strength]),
            new AiOperationContext("coaching-evaluative-vietnamese", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Fact]
    public void InterviewEvaluateAcceptsGroundedResultWordingWithGenericResultTerm()
    {
        const string candidateAnswer = "Latency giảm từ 500ms xuống 120ms.";
        const string strength = "Bạn nêu rõ kết quả cải thiện latency từ 500ms xuống 120ms.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, [strength]),
            new AiOperationContext("coaching-grounded-result-wording", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Theory]
    [InlineData("Bạn có kinh nghiệm vận hành production quy mô lớn.")]
    [InlineData("Bạn đã triển khai Kubernetes để xử lý deadlock.")]
    [InlineData("Bạn dẫn dắt team xử lý sự cố.")]
    [InlineData("Bạn giúp giảm 40% latency.")]
    public void InterviewEvaluateStillRejectsConcreteFactsAddedToEvaluativeStrengths(string strength)
    {
        const string candidateAnswer = "Tôi kiểm tra log và xác định nguyên nhân deadlock.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, [strength]),
            new AiOperationContext("coaching-concrete-fact-regression", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.False(result.IsValid);
        Assert.Equal("interview.strengths_ungrounded", result.FailureReason);
    }

    [Theory]
    [InlineData("Bạn có kinh nghiệm vận hành hệ thống production quy mô lớn.")]
    [InlineData("Bạn thể hiện năng lực leadership tốt.")]
    [InlineData("Bạn đã dẫn dắt team backend.")]
    [InlineData("You have strong backend production expertise.")]
    public void InterviewEvaluateRejectsFabricatedVietnameseStrengthClaims(string strength)
    {
        const string candidateAnswer = "Tôi đã xây API bằng ASP.NET Core và sử dụng PostgreSQL để lưu dữ liệu.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, [strength]),
            new AiOperationContext("coaching-fabricated-vietnamese-strength", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.False(result.IsValid);
        Assert.Equal("interview.strengths_ungrounded", result.FailureReason);
    }

    [Theory]
    [InlineData("Hãy nêu rõ trách nhiệm cá nhân của bạn trong dự án.")]
    [InlineData("Bạn nên định lượng tác động nếu có số liệu thực tế.")]
    [InlineData("Tập trung mô tả quyết định kỹ thuật bạn trực tiếp thực hiện.")]
    [InlineData("Có thể bổ sung một ví dụ cụ thể về vấn đề bạn đã giải quyết.")]
    [InlineData("Hãy   bổ sung   một ví dụ cụ thể nếu có.")]
    public void InterviewEvaluateAcceptsActionableVietnameseImprovements(string improvement)
    {
        const string candidateAnswer = "I debugged the API.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, ["The API debugging is clear."], [improvement]),
            new AiOperationContext("coaching-actionable-vietnamese", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Fact]
    public void InterviewEvaluateAcceptsWeakDirectivePrefixWhenFollowedBySubstantiveAction()
    {
        const string candidateAnswer = "I debugged the API.";
        var improvements = new[]
        {
            "Có thể làm rõ kết quả bằng một số liệu cụ thể nếu bạn có dữ liệu.",
            "Bạn nên bổ sung trách nhiệm cá nhân trong dự án.",
            "Hãy định lượng tác động nếu có số liệu."
        };

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, ["The API debugging is clear."], improvements),
            new AiOperationContext("coaching-weak-directive-prefix", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Theory]
    [InlineData("Phần trách nhiệm còn thiếu.")]
    [InlineData("Ví dụ chưa tốt.")]
    [InlineData("Kết quả chưa rõ.")]
    [InlineData("The result is unclear.")]
    public void InterviewEvaluateRejectsPassiveVietnameseImprovements(string improvement)
    {
        const string candidateAnswer = "I debugged the API.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, ["The API debugging is clear."], [improvement]),
            new AiOperationContext("coaching-passive-vietnamese", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.False(result.IsValid);
        Assert.Equal("interview.improvements_not_actionable", result.FailureReason);
    }

    [Theory]
    [InlineData("Kết quả có thể rõ hơn.")]
    [InlineData("Phần trả lời nên tốt hơn.")]
    public void InterviewEvaluateRejectsWeakDirectivePrefixWithoutSubstantiveAction(string improvement)
    {
        const string candidateAnswer = "I debugged the API.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, ["The API debugging is clear."], [improvement]),
            new AiOperationContext("coaching-weak-directive-only", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.False(result.IsValid);
        Assert.Equal("interview.improvements_not_actionable", result.FailureReason);
    }

    [Fact]
    public void InterviewEvaluateDoesNotUseRubricEvidenceToGroundStrengths()
    {
        const string candidateAnswer = "I used ASP.NET Core to build the API.";
        var rubricScores = new[]
        {
            new RubricScore("correctness", 80, "The candidate deployed Kubernetes in production."),
            new RubricScore("structure", 80, "The answer is organized."),
            new RubricScore("completeness", 80, "The API approach is covered."),
            new RubricScore("clarity", 80, "The explanation is clear.")
        };

        var rejected = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, ["Bạn đã triển khai Kubernetes trong production."], rubricScores: rubricScores),
            new AiOperationContext("coaching-rubric-evidence-fabricated", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.False(rejected.IsValid);
        Assert.Equal("interview.strengths_ungrounded", rejected.FailureReason);
    }

    [Theory]
    [InlineData("Terraform")]
    [InlineData("Elasticsearch")]
    [InlineData("Snowflake")]
    [InlineData("ArgoCD")]
    public void InterviewEvaluateRejectsUnseenTechnologyIdentifiersInStrengths(string technology)
    {
        const string candidateAnswer = "I built an API with ASP.NET Core.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, [$"You used {technology} to build the API with ASP.NET Core."]),
            new AiOperationContext("coaching-open-vocabulary-strength", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.False(result.IsValid);
        Assert.Equal("interview.strengths_ungrounded", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Theory]
    [InlineData("You used terraform to build the API with ASP.NET Core.")]
    [InlineData("Bạn sử dụng terraform để xây API bằng ASP.NET Core.")]
    [InlineData("Bạn đã dùng elasticsearch cùng ASP.NET Core.")]
    public void InterviewEvaluateRejectsLowercaseUnseenTechnologyIdentifiersInStrengths(string strength)
    {
        const string candidateAnswer = "I built an API with ASP.NET Core.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, [strength]),
            new AiOperationContext("coaching-lowercase-open-technology-strength", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.False(result.IsValid);
        Assert.Equal("interview.strengths_ungrounded", result.FailureReason);
        Assert.True(result.Repairable);
    }

    [Fact]
    public void InterviewEvaluateDoesNotRejectOrdinaryLowercaseEvaluativeProseAfterGenericVerb()
    {
        const string candidateAnswer = "I explained the API issue clearly.";
        const string strength = "You used clear language to explain the answer.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, [strength]),
            new AiOperationContext("coaching-ordinary-lowercase-prose", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Fact]
    public void InterviewEvaluateDoesNotRejectOrdinaryVietnameseProseAfterGenericVerb()
    {
        const string candidateAnswer = "Tôi đã nêu ví dụ cụ thể về cách xử lý lỗi.";
        const string strength = "Bạn dùng cách trình bày rõ ràng để mô tả ví dụ trong câu trả lời.";

        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, [strength]),
            new AiOperationContext("coaching-ordinary-vietnamese-prose", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(result.IsValid, result.FailureReason);
    }

    [Fact]
    public void InterviewEvaluateAcceptsGroundedBilingualTechnologyTermsButRejectsNewTechnology()
    {
        const string candidateAnswer = "Em dùng .NET, Redis cache và PostgreSQL cho project.";

        var accepted = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, ["Bạn đã nêu rõ .NET, Redis cache và PostgreSQL."]),
            new AiOperationContext("coaching-bilingual-technology", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.True(accepted.IsValid, accepted.FailureReason);

        var rejected = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            CreateCoachingEvaluation(candidateAnswer, ["Bạn đã nêu rõ Kubernetes và Redis cache."]),
            new AiOperationContext("coaching-bilingual-technology-fabricated", ExpectedStar: false, CandidateAnswer: candidateAnswer));

        Assert.False(rejected.IsValid);
        Assert.Equal("interview.strengths_ungrounded", rejected.FailureReason);
    }

    [Theory]
    [InlineData("interview.improved_answer_ungrounded")]
    [InlineData("interview.improved_answer_fabricated")]
    public void BuildRepairInstructionsForImprovedAnswerFailuresIsFailureSpecific(string failureReason)
    {
        var validation = AiValidationResult<AnswerEvaluation>.Failure(failureReason, "semantic", repairable: true);

        var repairInstructions = AiOperations.InterviewEvaluate.BuildRepairInstructions(validation, "Evaluate the interview answer.");

        Assert.Contains("IMPORTANT IMPROVED-ANSWER CORRECTION INSTRUCTION", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("only facts, technologies, responsibilities, actions, and outcomes explicitly present", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("placeholders", repairInstructions, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Preserve all already-valid rubric scores, rubric evidence, feedback, STAR, strengths, and improvements exactly", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("ONLY rewrite improvedAnswer", repairInstructions, StringComparison.Ordinal);
        Assert.Contains("Do not revise any other field", repairInstructions, StringComparison.Ordinal);
    }

    private static AnswerEvaluation CreateCoachingEvaluation(
        string candidateAnswer,
        IReadOnlyCollection<string> strengths,
        IReadOnlyCollection<string>? improvements = null,
        IReadOnlyCollection<RubricScore>? rubricScores = null)
    {
        return new AnswerEvaluation(
            rubricScores ??
            [
                new RubricScore("correctness", 80, candidateAnswer),
                new RubricScore("structure", 80, "The answer is structured."),
                new RubricScore("completeness", 80, "The answer is complete."),
                new RubricScore("clarity", 80, "The answer is clear.")
            ],
            "Good answer.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            strengths,
            improvements ?? ["Add one concrete example if available."],
            candidateAnswer);
    }
}
