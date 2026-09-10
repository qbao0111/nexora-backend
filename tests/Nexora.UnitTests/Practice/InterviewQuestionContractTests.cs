using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Practice;

public sealed class InterviewQuestionContractTests
{
    [Theory]
    [InlineData(1, InterviewQuestionValues.SelfIntroduction)]
    [InlineData(2, InterviewQuestionValues.BehavioralStar)]
    [InlineData(3, InterviewQuestionValues.MotivationRoleFit)]
    public void FreePrimaryQuestionTopicsAreExplicit(int sequence, string expectedTopic)
    {
        Assert.Equal(expectedTopic, InterviewQuestionValues.PrimaryTopicForSequence(sequence));
    }

    [Fact]
    public void SequenceDoesNotDefineQuestionKind()
    {
        Assert.True(InterviewQuestionValues.IsSupportedKind(InterviewQuestionValues.Primary));
        Assert.True(InterviewQuestionValues.IsSupportedKind(InterviewQuestionValues.Followup));
    }

    [Fact]
    public void FirstQuestionPromptTreatsServerOwnedTopicAsAuthoritative()
    {
        Assert.Equal("interview-first-question-v3", AiOperations.InterviewFirstQuestion.PromptVersion);
        Assert.Contains("question-topic", AiOperations.InterviewFirstQuestion.Instructions, StringComparison.Ordinal);
        Assert.Contains("authoritative", AiOperations.InterviewFirstQuestion.Instructions, StringComparison.Ordinal);
        Assert.Contains("never infer semantic topic", AiOperations.InterviewFirstQuestion.Instructions, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("technical", false, false, InterviewQuestionValues.Technical)]
    [InlineData("behavioral", false, false, InterviewQuestionValues.Behavioral)]
    [InlineData("scenario", false, false, InterviewQuestionValues.Scenario)]
    [InlineData("technical", true, false, InterviewQuestionValues.CvTargeted)]
    [InlineData("behavioral", false, true, InterviewQuestionValues.JdTargeted)]
    public void PaidContinuationTopicUsesServerOwnedContext(
        string interviewType,
        bool hasResume,
        bool hasJobDescription,
        string expectedTopic)
    {
        Assert.Equal(expectedTopic, InterviewQuestionValues.PaidTopicForContext(interviewType, hasResume, hasJobDescription));
    }
}
