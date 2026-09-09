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

    private static AnswerEvaluation TechnicalEvaluation(string? scoreScale) => new(
        [
            new RubricScore("correctness", 80, "Evidence"),
            new RubricScore("structure", 80, "Evidence"),
            new RubricScore("completeness", 80, "Evidence"),
            new RubricScore("clarity", 80, "Evidence")
        ],
        "Grounded feedback",
        null,
        scoreScale);

    private sealed class MockAiProvider : IAiProvider
    {
        public string ModelVersion => "mock-gemini";
        public List<AiRequest> Requests { get; } = [];
        public int CallCount => Requests.Count;

        private readonly Queue<object> _queue = new();

        public void EnqueueResult(object result) => _queue.Enqueue(result);
        public void EnqueueException(Exception exception) => _queue.Enqueue(exception);

        public Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_queue.Count == 0)
                throw new InvalidOperationException("No mock result queued.");
            var item = _queue.Dequeue();
            if (item is Exception ex)
                throw ex;
            return Task.FromResult((T)item);
        }
    }
}
