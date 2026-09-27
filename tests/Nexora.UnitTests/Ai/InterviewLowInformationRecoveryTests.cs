using Nexora.Business.Ai;

namespace Nexora.UnitTests.Ai;

public sealed class InterviewLowInformationRecoveryTests
{
    [Theory]
    [InlineData("alo alo")]
    [InlineData("tôi không biết")]
    [InlineData("ngại quá")]
    public void MultipleCoachingDefectsConvergeInOneTerminalRecovery(string candidateAnswer)
    {
        var operation = AiOperations.InterviewEvaluate;
        var context = new AiOperationContext("multi-coaching", ExpectedStar: false, CandidateAnswer: candidateAnswer);
        var raw = LowInformationEvaluation(candidateAnswer) with
        {
            Strengths = ["Bạn thể hiện khả năng lãnh đạo và dẫn dắt dự án tốt."],
            ImprovedAnswer = "Tôi đã dẫn dắt đội ngũ triển khai hệ thống production thành công."
        };
        var invalid = operation.NormalizeAndValidate(raw, context);
        Assert.False(invalid.IsValid);
        var recovery = operation.TryRecoverTerminalValidation(raw, context, invalid);
        Assert.NotNull(recovery);
        Assert.True(recovery.IsValid, recovery.FailureReason);
        Assert.Empty(recovery.NormalizedValue!.Strengths!);
        Assert.Single(recovery.NormalizedValue.Improvements!);
        Assert.Contains("Nêu trực tiếp", recovery.NormalizedValue.Improvements!.Single(), StringComparison.Ordinal);
        Assert.Equal(candidateAnswer, recovery.NormalizedValue.ImprovedAnswer);
        Assert.Equal(raw.Scores, recovery.NormalizedValue.Scores);
        Assert.Equal(raw.Feedback, recovery.NormalizedValue.Feedback);
        Assert.False(recovery.NormalizedValue.Star!.Applicable);

    }

    [Fact]
    public void OversizedTypedImprovedAnswerUsesBoundedOriginalAnswer()
    {
        var context = new AiOperationContext("oversized", ExpectedStar: false, CandidateAnswer: "alo alo");
        var raw = LowInformationEvaluation("alo alo") with
        {
            Improvements = ["Nêu trực tiếp câu trả lời."],
            ImprovedAnswer = new string('a', 4_001)
        };
        var invalid = AiOperations.InterviewEvaluate.NormalizeAndValidate(raw, context);
        Assert.Equal("interview.improved_answer_too_long", invalid.FailureReason);
        var recovery = AiOperations.InterviewEvaluate.TryRecoverTerminalValidation(raw, context, invalid);
        Assert.True(recovery!.IsValid, recovery.FailureReason);
        Assert.Equal("alo alo", recovery.NormalizedValue!.ImprovedAnswer);
    }

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
