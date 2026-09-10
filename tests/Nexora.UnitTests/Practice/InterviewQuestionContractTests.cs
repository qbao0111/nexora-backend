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
}
