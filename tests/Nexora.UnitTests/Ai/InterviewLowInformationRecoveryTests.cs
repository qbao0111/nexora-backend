using Nexora.Business.Ai;

namespace Nexora.UnitTests.Ai;

public sealed class InterviewLowInformationRecoveryTests
{
    [Theory]
    [InlineData("alo alo")]
    [InlineData("tôi không biết")]
    [InlineData("ngại quá")]
    public void TerminalImprovementsFailureUsesDeterministicActionableAdvice(string candidateAnswer)
    {
        var recovery = AiOperations.InterviewEvaluate.TryRecoverTerminalValidation(
            LowInformationEvaluation(candidateAnswer),
            new AiOperationContext("low-information-terminal", ExpectedStar: false, CandidateAnswer: candidateAnswer),
            AiValidationResult<AnswerEvaluation>.Failure(
                "interview.improvements_not_actionable",
                "semantic",
                repairable: true));

        Assert.NotNull(recovery);
        Assert.True(recovery.IsValid, recovery.FailureReason);
        Assert.Empty(recovery.NormalizedValue!.Strengths!);
        Assert.Equal(candidateAnswer, recovery.NormalizedValue.ImprovedAnswer);
        var improvement = Assert.Single(recovery.NormalizedValue.Improvements!);
        Assert.Contains("Nêu trực tiếp", improvement, StringComparison.Ordinal);
        Assert.DoesNotContain("dự án", improvement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ConciseTechnicalAnswerRemainsValidWhenEvaluationIsValid()
    {
        var result = AiOperations.InterviewEvaluate.NormalizeAndValidate(
            LowInformationEvaluation("O(n log n)") with
            {
                Improvements = ["Giải thích rõ thuật toán hoặc trade-off chính của câu trả lời."],
                ImprovedAnswer = "O(n log n)"
            },
            new AiOperationContext("concise-technical-answer", ExpectedStar: false, CandidateAnswer: "O(n log n)"));

        Assert.True(result.IsValid, result.FailureReason);
    }

    private static AnswerEvaluation LowInformationEvaluation(string candidateAnswer) => new(
        [
            new RubricScore("correctness", 20, candidateAnswer),
            new RubricScore("structure", 10, candidateAnswer),
            new RubricScore("completeness", 10, candidateAnswer),
            new RubricScore("clarity", 20, candidateAnswer)
        ],
        "Câu trả lời chưa cung cấp đủ bằng chứng để đánh giá cao.",
        null,
        AiOperations.ScoreScale,
        [],
        ["Kết quả chưa rõ."],
        candidateAnswer);
}
