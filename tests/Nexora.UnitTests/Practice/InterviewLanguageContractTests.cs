using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Practice;

public sealed class InterviewLanguageContractTests
{
    [Theory]
    [InlineData(null, InterviewLanguageValues.Vietnamese)]
    [InlineData("", InterviewLanguageValues.Vietnamese)]
    [InlineData(" vi-vn ", InterviewLanguageValues.Vietnamese)]
    [InlineData("en-us", InterviewLanguageValues.English)]
    public void SupportedInterviewLanguagesNormalizeToCanonicalValues(string? input, string expected)
    {
        Assert.True(InterviewLanguageValues.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void UnsupportedInterviewLanguageFailsClosed()
    {
        Assert.False(InterviewLanguageValues.TryNormalize("zh-CN", out var normalized));
        Assert.Empty(normalized);
    }

    [Fact]
    public void InterviewContextIsExplicitlyLanguageOwned()
    {
        var context = new AiOperationContext("correlation", InterviewLanguage: InterviewLanguageValues.English);

        Assert.Equal(InterviewLanguageValues.English, context.InterviewLanguage);
    }

    [Fact]
    public void InterviewPromptsUseTheExplicitLanguageContext()
    {
        var instructions = new[]
        {
            AiOperations.InterviewFirstQuestion.Instructions,
            AiOperations.InterviewFollowup.Instructions,
            AiOperations.InterviewEvaluate.Instructions,
            AiOperations.InterviewReport.Instructions
        };

        Assert.All(instructions, value =>
        {
            Assert.Contains("interview language supplied in context is authoritative", value, StringComparison.Ordinal);
            Assert.Contains("Do not infer language", value, StringComparison.Ordinal);
        });
    }
}
