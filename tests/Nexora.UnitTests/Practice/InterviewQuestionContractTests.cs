using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Practice;

public sealed class InterviewQuestionContractTests
{
    [Theory]
    [InlineData("technical", false, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.Technical, InterviewQuestionValues.Technical)]
    [InlineData("technical", true, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.Technical, InterviewQuestionValues.CvTargeted)]
    [InlineData("technical", false, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.Technical, InterviewQuestionValues.JdTargeted)]
    [InlineData("technical", true, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.Technical, InterviewQuestionValues.JdTargeted)]
    [InlineData("behavioral", false, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.BehavioralStar, InterviewQuestionValues.MotivationRoleFit)]
    [InlineData("behavioral", true, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.BehavioralStar, InterviewQuestionValues.MotivationRoleFit)]
    [InlineData("behavioral", false, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.BehavioralStar, InterviewQuestionValues.MotivationRoleFit)]
    [InlineData("behavioral", true, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.BehavioralStar, InterviewQuestionValues.MotivationRoleFit)]
    [InlineData("scenario", false, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.Scenario, InterviewQuestionValues.Scenario)]
    [InlineData("scenario", true, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.Scenario, InterviewQuestionValues.Scenario)]
    [InlineData("scenario", false, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.Scenario, InterviewQuestionValues.Scenario)]
    [InlineData("scenario", true, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.Scenario, InterviewQuestionValues.Scenario)]
    [InlineData("cv_targeted", false, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.CvTargeted, InterviewQuestionValues.CvTargeted)]
    [InlineData("cv_targeted", true, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.CvTargeted, InterviewQuestionValues.CvTargeted)]
    [InlineData("cv_targeted", false, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.CvTargeted, InterviewQuestionValues.CvTargeted)]
    [InlineData("cv_targeted", true, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.CvTargeted, InterviewQuestionValues.CvTargeted)]
    [InlineData("jd_targeted", false, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.JdTargeted, InterviewQuestionValues.JdTargeted)]
    [InlineData("jd_targeted", true, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.JdTargeted, InterviewQuestionValues.JdTargeted)]
    [InlineData("jd_targeted", false, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.JdTargeted, InterviewQuestionValues.JdTargeted)]
    [InlineData("jd_targeted", true, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.JdTargeted, InterviewQuestionValues.JdTargeted)]
    [InlineData("motivation_role_fit", false, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.MotivationRoleFit, InterviewQuestionValues.MotivationRoleFit)]
    [InlineData("motivation_role_fit", true, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.MotivationRoleFit, InterviewQuestionValues.MotivationRoleFit)]
    [InlineData("motivation_role_fit", false, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.MotivationRoleFit, InterviewQuestionValues.MotivationRoleFit)]
    [InlineData("motivation_role_fit", true, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.MotivationRoleFit, InterviewQuestionValues.MotivationRoleFit)]
    [InlineData("self_introduction", false, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.MotivationRoleFit, InterviewQuestionValues.Behavioral)]
    [InlineData("self_introduction", true, false, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.MotivationRoleFit, InterviewQuestionValues.CvTargeted)]
    [InlineData("self_introduction", false, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.MotivationRoleFit, InterviewQuestionValues.JdTargeted)]
    [InlineData("self_introduction", true, true, InterviewQuestionValues.SelfIntroduction, InterviewQuestionValues.MotivationRoleFit, InterviewQuestionValues.JdTargeted)]
    public void FreePrimaryQuestionTopicsFollowTheSelectedInterviewTypeAndContext(
        string interviewType,
        bool hasResume,
        bool hasJobDescription,
        string expectedQ1,
        string expectedQ2,
        string expectedQ3)
    {
        Assert.Equal(expectedQ1, InterviewQuestionValues.FreePrimaryTopicForSequence(interviewType, 1, hasResume, hasJobDescription));
        Assert.Equal(expectedQ2, InterviewQuestionValues.FreePrimaryTopicForSequence(interviewType, 2, hasResume, hasJobDescription));
        Assert.Equal(expectedQ3, InterviewQuestionValues.FreePrimaryTopicForSequence(interviewType, 3, hasResume, hasJobDescription));
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
        Assert.Equal("interview-first-question-v5", AiOperations.InterviewFirstQuestion.PromptVersion);
        Assert.Contains("question-topic", AiOperations.InterviewFirstQuestion.Instructions, StringComparison.Ordinal);
        Assert.Contains("authoritative", AiOperations.InterviewFirstQuestion.Instructions, StringComparison.Ordinal);
        Assert.Contains("never infer semantic topic", AiOperations.InterviewFirstQuestion.Instructions, StringComparison.Ordinal);
        Assert.Contains(AiLanguagePolicy.VietnameseUserFacingInstruction, AiOperations.InterviewFirstQuestion.Instructions, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("technical", InterviewQuestionValues.Technical, InterviewQuestionValues.Scenario)]
    [InlineData("behavioral", InterviewQuestionValues.Behavioral, InterviewQuestionValues.Scenario)]
    [InlineData("scenario", InterviewQuestionValues.Technical, InterviewQuestionValues.Behavioral)]
    public void FiveQuestionBlueprintUsesDifferentPaidSlots(
        string interviewType, string expectedFourth, string expectedFifth)
    {
        Assert.Equal(expectedFourth, InterviewQuestionValues.TopicForSequence(interviewType, 4, false, false));
        Assert.Equal(expectedFifth, InterviewQuestionValues.TopicForSequence(interviewType, 5, false, false));
        Assert.Equal(5, InterviewQuestionValues.MaxQuestionsPerSession);
    }

    [Theory]
    [InlineData("Hãy trình bày cách bạn phân tích yêu cầu API.", "BẠN phân tích yêu cầu API như thế nào?")]
    [InlineData("Hãy trình bày cách bạn phân tích yêu cầu API.", "Hãy trình bày cách bạn phân tích yêu cầu API!")]
    public void ObviousRepeatedQuestionIsRejectedWithinStructuredExecutorValidation(string previous, string candidate)
    {
        var context = new AiOperationContext("test", PreviousQuestions: [previous]);
        var result = AiOperations.InterviewFirstQuestion.NormalizeAndValidate(new GeneratedQuestion(candidate), context);
        Assert.False(result.IsValid);
        Assert.Equal("question.duplicate", result.FailureReason);
        Assert.True(result.Repairable);
    }
}
