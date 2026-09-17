using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Business.Ai;
using Nexora.Business.Common;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Ai;

public sealed class StructuredAiExecutorTests
{
    [Fact]
    public async Task ExecuteAsyncSucceedsOnFirstAttemptWithoutRepair()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(new GeneratedQuestion("What is polymorphism?"));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            "role: Backend Engineer",
            new AiOperationContext("corr-1"),
            CancellationToken.None);

        Assert.Equal("What is polymorphism?", result.Value.Content);
        Assert.False(result.RepairUsed);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, fakeProvider.CallCount);
    }

    [Fact]
    public async Task InterviewReportRepairsRepairableSemanticFailureOnSecondAttempt()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(InvalidInterviewReport());
        fakeProvider.EnqueueResult(ValidInterviewReport());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewReport,
            "official interview transcript",
            InterviewReportContext("I debugged the API."),
            CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Contains("report.strengths_invalid", fakeProvider.Requests[1].Instructions);
    }

    [Fact]
    public async Task InterviewReportStopsAfterTwoInvalidAttemptsWithoutThirdCall()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(InvalidInterviewReport());
        fakeProvider.EnqueueResult(InvalidInterviewReport());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewReport,
            "official interview transcript",
            InterviewReportContext("I debugged the API."),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public async Task InterviewReportValidFirstAttemptUsesOneProviderCall()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(ValidInterviewReport());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewReport,
            "official interview transcript",
            InterviewReportContext("I debugged the API."),
            CancellationToken.None);

        Assert.False(result.RepairUsed);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, fakeProvider.CallCount);
    }

    [Fact]
    public async Task InterviewReportRepairsGroundingFailureOnSecondAttempt()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(ValidInterviewReport("I migrated Kubernetes workloads."));
        fakeProvider.EnqueueResult(ValidInterviewReport());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewReport,
            "official interview transcript",
            InterviewReportContext("I debugged the API."),
            CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Contains("report.rubric_evidence_ungrounded", fakeProvider.Requests[1].Instructions);
    }

    [Fact]
    public async Task ExecuteAsyncRepairsSemanticFailureOnSecondAttempt()
    {
        var fakeProvider = new MockAiProvider();
        // Attempt 1 returns blank question (semantic failure)
        fakeProvider.EnqueueResult(new GeneratedQuestion(""));
        // Attempt 2 returns valid question
        fakeProvider.EnqueueResult(new GeneratedQuestion("What is encapsulation?"));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            "role: Backend Engineer",
            new AiOperationContext("corr-2"),
            CancellationToken.None);

        Assert.Equal("What is encapsulation?", result.Value.Content);
        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);

        // Verify repair instructions were injected on attempt 2
        var secondRequest = fakeProvider.Requests[1];
        Assert.NotNull(secondRequest.Instructions);
        Assert.Contains("IMPORTANT CORRECTION INSTRUCTION", secondRequest.Instructions);
    }

    [Fact]
    public async Task ExecuteAsyncCapsRetriesAtMaximumTwoAttemptsOnPersistentSemanticFailure()
    {
        var fakeProvider = new MockAiProvider();
        // Both attempts return blank question
        fakeProvider.EnqueueResult(new GeneratedQuestion(""));
        fakeProvider.EnqueueResult(new GeneratedQuestion("   "));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            "role: Backend Engineer",
            new AiOperationContext("corr-3"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", ex.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRetryUnrepairableFailure()
    {
        var fakeProvider = new MockAiProvider();
        // Mock a profile with excessive summaries that is marked unrepairable
        var excessiveProfile = new ResumeProfile(
            new string('X', 5000), // Exceeds 3,000 char limit (unrepairable in ResumeProfile definition)
            [], [], [], [], [], []);
        fakeProvider.EnqueueResult(excessiveProfile);
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.ResumeProfile,
            "resume-text",
            new AiOperationContext("corr-4"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", ex.Code);
        Assert.Equal(1, fakeProvider.CallCount); // Exactly 1 call, no wasteful retry
    }

    [Fact]
    public async Task ExecuteAsyncRetriesTransientProviderErrorAndSucceeds()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.RateLimited, "Rate limit"));
        fakeProvider.EnqueueResult(new GeneratedQuestion("What is DI?"));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            "role: Backend Engineer",
            new AiOperationContext("corr-5"),
            CancellationToken.None);

        Assert.Equal("What is DI?", result.Value.Content);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public async Task ResumeAnalysisRetriesOutputTruncationAtLargerBudgetOnlyOnce()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated",
            retryHint: AiProviderRetryHint.OutputTruncated));
        fakeProvider.EnqueueResult(ValidResumeAnalysis());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.ResumeAnalysis,
            "candidate input",
            ResumeAnalysisContext("resume-truncation"),
            CancellationToken.None);

        Assert.Equal(2, result.Attempts);
        Assert.False(result.RepairUsed);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Equal(4_096, fakeProvider.Requests[0].MaxOutputTokens);
        Assert.Equal(8_192, fakeProvider.Requests[1].MaxOutputTokens);
        Assert.All(fakeProvider.Requests, request => Assert.Null(request.ReasoningEffortOverride));
    }

    [Fact]
    public async Task ResumeAnalysisStopsAfterTwoOutputTruncationsWithoutFabricatedResult()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated 1",
            retryHint: AiProviderRetryHint.OutputTruncated));
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated 2",
            retryHint: AiProviderRetryHint.OutputTruncated));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.ResumeAnalysis,
            "candidate input",
            ResumeAnalysisContext("resume-truncation-terminal"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Equal(4_096, fakeProvider.Requests[0].MaxOutputTokens);
        Assert.Equal(8_192, fakeProvider.Requests[1].MaxOutputTokens);
    }

    [Fact]
    public async Task ScenarioEvaluationRetriesTruncatedFirstAttemptWithinTwoCallBudget()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated",
            retryHint: AiProviderRetryHint.OutputTruncated));
        fakeProvider.EnqueueResult(ValidScenarioEvaluation());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.ScenarioEvaluate,
            "scenario answer",
            new AiOperationContext("scenario-truncation-retry"),
            CancellationToken.None);

        Assert.Equal(72, result.Value.OverallScore);
        Assert.False(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.All(fakeProvider.Requests, request => Assert.Equal(4_000, request.MaxOutputTokens));
        Assert.All(fakeProvider.Requests, request => Assert.Null(request.ReasoningEffortOverride));
    }

    [Fact]
    public async Task ScenarioEvaluationStopsAfterTwoOutputTruncationsWithoutFabricatedResult()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated 1",
            retryHint: AiProviderRetryHint.OutputTruncated));
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated 2",
            retryHint: AiProviderRetryHint.OutputTruncated));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.ScenarioEvaluate,
            "scenario answer",
            new AiOperationContext("scenario-truncation-terminal"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.All(fakeProvider.Requests, request => Assert.Equal(4_000, request.MaxOutputTokens));
    }

    [Fact]
    public async Task ScenarioEvaluationCompletesNormallyWithinConfiguredBudget()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(ValidScenarioEvaluation());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.ScenarioEvaluate,
            "scenario answer",
            new AiOperationContext("scenario-normal-completion"),
            CancellationToken.None);

        Assert.Equal(72, result.Value.OverallScore);
        Assert.Equal(1, result.Attempts);
        Assert.False(result.RepairUsed);
        Assert.Equal(1, fakeProvider.CallCount);
        Assert.Equal(4_000, fakeProvider.Requests[0].MaxOutputTokens);
    }

    [Fact]
    public async Task FieldBenchmarkResumeAnalysisRetriesOutputTruncationAtLargerBudgetOnlyOnce()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated",
            retryHint: AiProviderRetryHint.OutputTruncated));
        fakeProvider.EnqueueResult(ValidFieldBenchmarkAnalysis());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.ResumeAnalysisFieldBenchmark,
            "candidate input",
            FieldBenchmarkResumeAnalysisContext("field-resume-truncation"),
            CancellationToken.None);

        Assert.Equal(2, result.Attempts);
        Assert.False(result.RepairUsed);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Equal(4_096, fakeProvider.Requests[0].MaxOutputTokens);
        Assert.Equal(8_192, fakeProvider.Requests[1].MaxOutputTokens);
    }

    [Fact]
    public async Task FieldBenchmarkResumeAnalysisStopsAfterTwoOutputTruncationsWithoutFabricatedResult()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated 1",
            retryHint: AiProviderRetryHint.OutputTruncated));
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "truncated 2",
            retryHint: AiProviderRetryHint.OutputTruncated));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.ResumeAnalysisFieldBenchmark,
            "candidate input",
            FieldBenchmarkResumeAnalysisContext("field-resume-truncation-terminal"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Equal(4_096, fakeProvider.Requests[0].MaxOutputTokens);
        Assert.Equal(8_192, fakeProvider.Requests[1].MaxOutputTokens);
    }

    [Fact]
    public async Task ResumeAnalysisSemanticRepairKeepsInitialBudget()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(new ResumeAnalysisOutput(
            [],
            [],
            [],
            MatchScore: 50,
            Summary: "Grounded summary",
            MatchedKeywordsOrSkills: [],
            MissingKeywordsOrSkills: [],
            SectionFeedback: ["Grounded section feedback."],
            Breakdown: JobBreakdown(),
            Mode: ResumeAnalysisModes.JobTargeted));
        fakeProvider.EnqueueResult(ValidResumeAnalysis());
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.ResumeAnalysis,
            "candidate input",
            ResumeAnalysisContext("resume-semantic-repair"),
            CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal(4_096, fakeProvider.Requests[0].MaxOutputTokens);
        Assert.Equal(4_096, fakeProvider.Requests[1].MaxOutputTokens);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRetryNonTransientProviderError()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.Authentication, "Invalid API key"));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            "role: Backend Engineer",
            new AiOperationContext("corr-6"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", ex.Code);
        Assert.Equal(1, fakeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsyncMapsExhaustedRateLimitToAiRateLimited()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.RateLimited, "Rate limit 1"));
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.RateLimited, "Rate limit 2"));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            "role: Backend Engineer",
            new AiOperationContext("corr-7"),
            CancellationToken.None));

        Assert.Equal("AI_RATE_LIMITED", ex.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsyncMapsExhaustedTimeoutToAiProviderUnavailable()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.Timeout, "Timeout 1"));
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.Timeout, "Timeout 2"));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var ex = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            "role: Backend Engineer",
            new AiOperationContext("corr-8"),
            CancellationToken.None));

        Assert.Equal("AI_PROVIDER_UNAVAILABLE", ex.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Theory]
    [InlineData(AiProviderFailureKind.Timeout)]
    [InlineData(AiProviderFailureKind.RateLimited)]
    [InlineData(AiProviderFailureKind.Unavailable)]
    public async Task ExecuteAsyncDoesNotApplyReasoningOverrideToNonInvalidResponseHint(AiProviderFailureKind failureKind)
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(
            failureKind,
            $"{failureKind} failure",
            retryHint: AiProviderRetryHint.LowerReasoningEffort));
        fakeProvider.EnqueueResult(new GeneratedQuestion("What is dependency injection?"));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewFirstQuestion,
            "role: Backend Engineer",
            new AiOperationContext("reasoning-hint-non-invalid"),
            CancellationToken.None);

        Assert.Equal(2, result.Attempts);
        Assert.False(result.RepairUsed);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Null(fakeProvider.Requests[0].ReasoningEffortOverride);
        Assert.Null(fakeProvider.Requests[1].ReasoningEffortOverride);
    }

    [Fact]
    public async Task ExecuteAsyncRepairsInvalidScoreScaleOnSecondAttempt()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(TechnicalEvaluation("1-5"));
        fakeProvider.EnqueueResult(TechnicalEvaluation(AiOperations.ScoreScale));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "technical answer",
            new AiOperationContext("scale-repair", ExpectedStar: false),
            CancellationToken.None);

        Assert.Equal(AiOperations.ScoreScale, result.Value.ScoreScale);
        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Contains("score.scale_invalid", fakeProvider.Requests[1].Instructions);
        Assert.Null(fakeProvider.Requests[0].ReasoningEffortOverride);
        Assert.Null(fakeProvider.Requests[1].ReasoningEffortOverride);
    }

    [Fact]
    public async Task ExecuteAsyncRepairsNullRubricItemOnSecondAttemptWithoutThirdCall()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(TechnicalEvaluationWithNullRubricItem());
        fakeProvider.EnqueueResult(TechnicalEvaluation(AiOperations.ScoreScale));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "technical answer",
            new AiOperationContext("null-rubric-item-repair", ExpectedStar: false),
            CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Contains("rubric.item_invalid", fakeProvider.Requests[1].Instructions);
    }

    [Fact]
    public async Task ExecuteAsyncRepairsInvalidImprovementsOnSecondAttempt()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(TechnicalEvaluation(AiOperations.ScoreScale) with
        {
            Improvements = [],
            ImprovedAnswer = "Grounded answer"
        });
        fakeProvider.EnqueueResult(TechnicalEvaluation(AiOperations.ScoreScale));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "Grounded answer",
            new AiOperationContext("improvements-repair", ExpectedStar: false, CandidateAnswer: "Grounded answer"),
            CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Contains("interview.improvements_invalid", fakeProvider.Requests[1].Instructions, StringComparison.Ordinal);
        Assert.NotEmpty(result.Value.Improvements!);
    }

    [Fact]
    public async Task ExecuteAsyncRepairsUngroundedImprovedAnswerOnSecondAttempt()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(TechnicalEvaluation(AiOperations.ScoreScale) with { ImprovedAnswer = "Clear structured response" });
        fakeProvider.EnqueueResult(TechnicalEvaluation(AiOperations.ScoreScale) with { ImprovedAnswer = "Grounded answer" });
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "Grounded answer",
            new AiOperationContext("improved-answer-repair", ExpectedStar: false, CandidateAnswer: "Grounded answer"),
            CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal("Grounded answer", result.Value.ImprovedAnswer);
        Assert.Contains("interview.improved_answer_ungrounded", fakeProvider.Requests[1].Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsyncFallsBackToCandidateAnswerWhenOnlyImprovedAnswerRemainsUngrounded()
    {
        var fakeProvider = new MockAiProvider();
        var ungrounded = TechnicalEvaluation(AiOperations.ScoreScale) with { ImprovedAnswer = "Clear structured response" };
        fakeProvider.EnqueueResult(ungrounded);
        fakeProvider.EnqueueResult(ungrounded);
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "Grounded answer",
            new AiOperationContext("improved-answer-fallback", ExpectedStar: false, CandidateAnswer: "Grounded answer"),
            CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Equal("Grounded answer", result.Value.ImprovedAnswer);
    }

    [Fact]
    public async Task ExecuteAsyncRecoversPriorEvaluationWhenSemanticRepairReturnsInvalidResponse()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.DelayPerCall = TimeSpan.FromMilliseconds(20);
        var firstEvaluation = TechnicalEvaluation(AiOperations.ScoreScale) with
        {
            Feedback = "Attempt-one feedback remains authoritative.",
            ImprovedAnswer = "Clear structured response"
        };
        fakeProvider.EnqueueResult(firstEvaluation);
        fakeProvider.EnqueueException(new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "AI provider returned an invalid structured response."));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "Grounded answer",
            new AiOperationContext("repair-provider-failure", ExpectedStar: false, CandidateAnswer: "Grounded answer"),
            CancellationToken.None);

        Assert.True(result.RepairUsed);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(2, fakeProvider.CallCount);
        Assert.Equal("mock-gemini", result.ModelVersion);
        Assert.Equal(AiOperations.InterviewEvaluate.PromptVersion, result.PromptVersion);
        Assert.Equal(AiOperations.InterviewEvaluate.SchemaVersion, result.SchemaVersion);
        Assert.Equal(AiOperations.InterviewEvaluate.RubricVersion, result.RubricVersion);
        Assert.True(result.LatencyMs >= 30);
        Assert.Contains("interview.improved_answer_ungrounded", fakeProvider.Requests[1].Instructions, StringComparison.Ordinal);
        Assert.Equal("Grounded answer", result.Value.ImprovedAnswer);
        Assert.Equal(firstEvaluation.Scores, result.Value.Scores);
        Assert.Equal(firstEvaluation.Feedback, result.Value.Feedback);
        Assert.Equal(firstEvaluation.Strengths, result.Value.Strengths);
        Assert.Equal(firstEvaluation.Improvements, result.Value.Improvements);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRecoverPriorOutputWhenRubricWasInvalid()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(TechnicalEvaluation(AiOperations.ScoreScale) with { Scores = [] });
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.InvalidResponse, "Invalid repair response."));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "Grounded answer",
            new AiOperationContext("invalid-rubric-provider-failure", ExpectedStar: false, CandidateAnswer: "Grounded answer"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotRecoverPriorOutputWhenImprovementsWereInvalid()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(TechnicalEvaluation(AiOperations.ScoreScale) with
        {
            Improvements = [],
            ImprovedAnswer = "Clear structured response"
        });
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.InvalidResponse, "Invalid repair response."));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "Grounded answer",
            new AiOperationContext("invalid-improvements-provider-failure", ExpectedStar: false, CandidateAnswer: "Grounded answer"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public void InterviewEvaluationRecoveryRevalidatesAllCoreFields()
    {
        var invalidRaw = TechnicalEvaluation(AiOperations.ScoreScale) with
        {
            Improvements = [],
            ImprovedAnswer = "Clear structured response"
        };
        var context = new AiOperationContext(
            "recovery-full-revalidation",
            ExpectedStar: false,
            CandidateAnswer: "Grounded answer");

        // The validator reports the first failure only; this synthetic prior result
        // exercises the defensive recovery boundary with a newly invalid core field.
        var recovery = AiOperations.InterviewEvaluate.TryRecoverTerminalValidation(
            invalidRaw,
            context,
            AiValidationResult<AnswerEvaluation>.Failure(
                "interview.improved_answer_ungrounded",
                "semantic",
                repairable: true));

        Assert.NotNull(recovery);
        Assert.False(recovery.IsValid);
        Assert.Equal("interview.improvements_invalid", recovery.FailureReason);
    }

    [Fact]
    public async Task ExecuteAsyncFailsClosedWhenBothInterviewProviderResponsesAreInvalid()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.InvalidResponse, "Invalid first response."));
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.InvalidResponse, "Invalid repair response."));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "Grounded answer",
            new AiOperationContext("both-provider-responses-invalid", ExpectedStar: false, CandidateAnswer: "Grounded answer"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsyncDoesNotApplyInterviewRecoveryToNonInterviewOperations()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.InvalidResponse, "Invalid first response."));
        fakeProvider.EnqueueException(new AiProviderException(AiProviderFailureKind.InvalidResponse, "Invalid repair response."));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.ResumeProfile,
            "candidate resume text",
            new AiOperationContext("non-interview-provider-failure"),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    [Fact]
    public async Task ExecuteAsyncFallbackPreservesAnExtremelyShortCandidateAnswerWithoutInventingFacts()
    {
        var fakeProvider = new MockAiProvider();
        var ungrounded = TechnicalEvaluation(AiOperations.ScoreScale) with
        {
            Scores =
            [
                new RubricScore("correctness", 10, "No useful evidence."),
                new RubricScore("structure", 10, "No structure."),
                new RubricScore("completeness", 10, "Incomplete."),
                new RubricScore("clarity", 10, "Unclear.")
            ],
            Strengths = [],
            ImprovedAnswer = "Clear structured response"
        };
        fakeProvider.EnqueueResult(ungrounded);
        fakeProvider.EnqueueResult(ungrounded);
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "x",
            new AiOperationContext("short-answer-fallback", ExpectedStar: false, CandidateAnswer: "x"),
            CancellationToken.None);

        Assert.Equal("x", result.Value.ImprovedAnswer);
        Assert.Empty(result.Value.Strengths!);
        Assert.All(result.Value.Scores, score => Assert.InRange(score.Score, 0, 59));
    }

    [Fact]
    public async Task ExecuteAsyncRejectsInvalidScoreScaleAfterExactlyTwoAttempts()
    {
        var fakeProvider = new MockAiProvider();
        fakeProvider.EnqueueResult(TechnicalEvaluation("1-5"));
        fakeProvider.EnqueueResult(TechnicalEvaluation(null));
        var executor = new StructuredAiExecutor(fakeProvider, NullLogger<StructuredAiExecutor>.Instance);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(
            AiOperations.InterviewEvaluate,
            "technical answer",
            new AiOperationContext("scale-invalid", ExpectedStar: false),
            CancellationToken.None));

        Assert.Equal("AI_OUTPUT_INVALID", exception.Code);
        Assert.Equal(2, fakeProvider.CallCount);
    }

    private static InterviewReportOutput ValidInterviewReport(string evidence = "I debugged the API.") => new(
        [
            new RubricScore("correctness", 80, evidence),
            new RubricScore("structure", 80, evidence),
            new RubricScore("completeness", 80, evidence),
            new RubricScore("clarity", 80, evidence)
        ],
        ["I debugged the API."],
        ["Add concrete evidence if available."],
        ["Add concrete evidence if available."],
        AiOperations.ScoreScale);

    private static InterviewReportOutput InvalidInterviewReport() => new(
        [
            new RubricScore("correctness", 80, "I debugged the API."),
            new RubricScore("structure", 80, "I debugged the API."),
            new RubricScore("completeness", 80, "I debugged the API."),
            new RubricScore("clarity", 80, "I debugged the API.")
        ],
        [],
        ["Add concrete evidence if available."],
        ["Add concrete evidence if available."],
        AiOperations.ScoreScale);

    private static AiOperationContext InterviewReportContext(string transcript) =>
        new("interview-report-retry", GroundingTranscript: transcript);

    private static AnswerEvaluation TechnicalEvaluation(string? scoreScale) => new(
        [
            new RubricScore("correctness", 80, "Evidence"),
            new RubricScore("structure", 80, "Evidence"),
            new RubricScore("completeness", 80, "Evidence"),
            new RubricScore("clarity", 80, "Evidence")
        ],
        "Grounded feedback",
        null,
        scoreScale,
        ["Grounded answer"],
        ["Add one concrete example if available"],
        "Grounded answer with evidence");

    private static AnswerEvaluation TechnicalEvaluationWithNullRubricItem() => new(
        [
            null!,
            new RubricScore("structure", 80, "Evidence"),
            new RubricScore("completeness", 80, "Evidence"),
            new RubricScore("clarity", 80, "Evidence")
        ],
        "Grounded feedback",
        null,
        AiOperations.ScoreScale,
        ["Grounded answer"],
        ["Add one concrete example if available"],
        "Grounded answer with evidence");

    private static ScenarioEvaluationResult ValidScenarioEvaluation() => new(
        72,
        [
            new ScenarioDimensionEvaluation("problem_analysis", 80, "Identifies the root cause.", "Structure the diagnosis in explicit steps."),
            new ScenarioDimensionEvaluation("communication", 70, "Explains the decision clearly.", "State the trade-off before the recommendation.")
        ],
        ["The response is grounded in the scenario."],
        ["The fallback plan is not explicit."],
        ["Add a measurable fallback and owner."],
        "The response is clear but should make the fallback measurable.",
        AiOperations.ScoreScale);

    private static AiOperationContext ResumeAnalysisContext(string correlationId) => new(
        correlationId,
        Metadata: new Dictionary<string, string>
        {
            [ResumeAnalysisMetadata.Mode] = ResumeAnalysisModes.JobTargeted
        });

    private static AiOperationContext FieldBenchmarkResumeAnalysisContext(string correlationId) => new(
        correlationId,
        Metadata: new Dictionary<string, string>
        {
            [ResumeAnalysisMetadata.Mode] = ResumeAnalysisModes.FieldBenchmark
        });

    private static ResumeAnalysisOutput ValidResumeAnalysis() => new(
        ["Strength"],
        ["Gap"],
        ["Recommendation"],
        MatchScore: 75,
        Summary: "Grounded summary",
        MatchedKeywordsOrSkills: [],
        MissingKeywordsOrSkills: [],
        SectionFeedback: ["Grounded section feedback."],
        Breakdown: JobBreakdown(),
        Mode: ResumeAnalysisModes.JobTargeted);

    private static Dictionary<string, int> JobBreakdown() => new()
    {
        ["technicalSkillMatch"] = 75,
        ["experienceRelevance"] = 70,
        ["impactEvidence"] = 65,
        ["clarity"] = 80,
        ["structure"] = 75
    };

    private static ResumeAnalysisOutput ValidFieldBenchmarkAnalysis() => new(
        ["Strong foundation"],
        ["Limited architecture ownership evidence"],
        ["Add a measurable architecture project"],
        ReadinessScore: 74,
        Summary: "The profile has a solid foundation for the target field.",
        SectionFeedback: ["Projects show relevant practice."],
        Breakdown: FieldBenchmarkBreakdown(),
        Mode: ResumeAnalysisModes.FieldBenchmark);

    private static Dictionary<string, int> FieldBenchmarkBreakdown() => new()
    {
        ["technicalFoundation"] = 82,
        ["projectEvidence"] = 72,
        ["experiencePresentation"] = 70,
        ["impactAchievements"] = 65,
        ["clarity"] = 80,
        ["roleAlignment"] = 76
    };

    private sealed class MockAiProvider : IAiProvider
    {
        public string ModelVersion => "mock-gemini";
        public List<AiRequest> Requests { get; } = [];
        public int CallCount => Requests.Count;
        public TimeSpan DelayPerCall { get; set; }

        private readonly Queue<object> _queue = new();

        public void EnqueueResult(object result) => _queue.Enqueue(result);
        public void EnqueueException(Exception exception) => _queue.Enqueue(exception);

        public async Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_queue.Count == 0)
                throw new InvalidOperationException("No mock result queued.");
            if (DelayPerCall > TimeSpan.Zero)
                await Task.Delay(DelayPerCall, cancellationToken);

            var item = _queue.Dequeue();
            if (item is Exception ex)
                throw ex;
            return (T)item;
        }
    }
}
