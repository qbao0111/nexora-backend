using Nexora.Business.Ai;
using Nexora.Business.Practice;

namespace Nexora.UnitTests.Ai;

public sealed class VietnameseOnlyPolicyTests
{
    [Fact]
    public void EveryUserFacingOperationUsesTheCentralVietnamesePolicy()
    {
        var operations = new[]
        {
            (Purpose: AiOperations.ResumeProfile.Purpose, Instructions: AiOperations.ResumeProfile.Instructions),
            (Purpose: AiOperations.ResumeAnalysis.Purpose, Instructions: AiOperations.ResumeAnalysis.Instructions),
            (Purpose: AiOperations.ResumeAnalysis.Purpose, Instructions: AiOperations.ResumeAnalysisFieldBenchmark.Instructions),
            (Purpose: AiOperations.InterviewFirstQuestion.Purpose, Instructions: AiOperations.InterviewFirstQuestion.Instructions),
            (Purpose: AiOperations.InterviewFollowup.Purpose, Instructions: AiOperations.InterviewFollowup.Instructions),
            (Purpose: AiOperations.InterviewEvaluate.Purpose, Instructions: AiOperations.InterviewEvaluate.Instructions),
            (Purpose: AiOperations.InterviewReport.Purpose, Instructions: AiOperations.InterviewReport.Instructions),
            (Purpose: AiOperations.ScenarioEvaluate.Purpose, Instructions: AiOperations.ScenarioEvaluate.Instructions),
            (Purpose: AiOperations.StarEvaluate.Purpose, Instructions: AiOperations.StarEvaluate.Instructions)
        };

        Assert.All(operations, operation =>
        {
            Assert.False(string.IsNullOrWhiteSpace(operation.Purpose));
            Assert.Contains(AiLanguagePolicy.VietnameseUserFacingInstruction, operation.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("same language as", operation.Instructions, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("language of the", operation.Instructions, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("SA")]
    [InlineData("Backend Developer")]
    [InlineData("软件工程师")]
    public void InterviewQuestionInstructionDoesNotInferLanguageFromSourceContext(string suppliedRole)
    {
        _ = suppliedRole;

        Assert.Contains(AiLanguagePolicy.VietnameseUserFacingInstruction, AiOperations.InterviewFirstQuestion.Instructions, StringComparison.Ordinal);
        Assert.Contains(AiLanguagePolicy.VietnameseUserFacingInstruction, AiOperations.InterviewFollowup.Instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptSchemaAndRubricVersionsAreExplicit()
    {
        Assert.Equal("resume-profile-v3", AiOperations.ResumeProfile.PromptVersion);
        Assert.Equal("resume-analysis-job-targeted-v3", AiOperations.ResumeAnalysis.PromptVersion);
        Assert.Equal("resume-analysis-field-benchmark-v3", AiOperations.ResumeAnalysisFieldBenchmark.PromptVersion);
        Assert.Equal("interview-first-question-v5", AiOperations.InterviewFirstQuestion.PromptVersion);
        Assert.Equal("interview-followup-v4", AiOperations.InterviewFollowup.PromptVersion);
        Assert.Equal("interview-eval-v10", AiOperations.InterviewEvaluate.PromptVersion);
        Assert.Equal("interview-report-v4", AiOperations.InterviewReport.PromptVersion);
        Assert.Equal("scenario-eval-v3", AiOperations.ScenarioEvaluate.PromptVersion);
        Assert.Equal("star-eval-v4", AiOperations.StarEvaluate.PromptVersion);

        Assert.Equal("resume-profile-v2", AiOperations.ResumeProfile.SchemaVersion);
        Assert.Equal("profile-v2", AiOperations.ResumeProfile.RubricVersion);
        Assert.Equal("resume-analysis-job-targeted-v2", AiOperations.ResumeAnalysis.SchemaVersion);
        Assert.Equal("analysis-job-targeted-v2", AiOperations.ResumeAnalysis.RubricVersion);
        Assert.Equal("resume-analysis-field-benchmark-v2", AiOperations.ResumeAnalysisFieldBenchmark.SchemaVersion);
        Assert.Equal("analysis-field-benchmark-v2", AiOperations.ResumeAnalysisFieldBenchmark.RubricVersion);
        Assert.Equal("interview-first-question-v2", AiOperations.InterviewFirstQuestion.SchemaVersion);
        Assert.Equal("rubric-v2", AiOperations.InterviewFirstQuestion.RubricVersion);
        Assert.Equal("interview-followup-v2", AiOperations.InterviewFollowup.SchemaVersion);
        Assert.Equal("rubric-v2", AiOperations.InterviewFollowup.RubricVersion);
        Assert.Equal("interview-eval-v6", AiOperations.InterviewEvaluate.SchemaVersion);
        Assert.Equal("rubric-v2", AiOperations.InterviewEvaluate.RubricVersion);
        Assert.Equal("interview-report-v2", AiOperations.InterviewReport.SchemaVersion);
        Assert.Equal("rubric-v2", AiOperations.InterviewReport.RubricVersion);
        Assert.Equal("scenario-eval-v2", AiOperations.ScenarioEvaluate.SchemaVersion);
        Assert.Equal("scenario-rubric-v2", AiOperations.ScenarioEvaluate.RubricVersion);
        Assert.Equal("star-eval-v3", AiOperations.StarEvaluate.SchemaVersion);
        Assert.Equal("star-rubric-v2", AiOperations.StarEvaluate.RubricVersion);
    }

    [Fact]
    public void RepairInstructionsRetainVietnamesePolicy()
    {
        AssertRepair(AiOperations.ResumeProfile);
        AssertRepair(AiOperations.ResumeAnalysis);
        AssertRepair(AiOperations.ResumeAnalysisFieldBenchmark);
        AssertRepair(AiOperations.InterviewFirstQuestion);
        AssertRepair(AiOperations.InterviewFollowup);
        AssertRepair(AiOperations.InterviewEvaluate);
        AssertRepair(AiOperations.InterviewReport);
        AssertRepair(AiOperations.ScenarioEvaluate);
        AssertRepair(AiOperations.StarEvaluate);
    }

    [Fact]
    public void LanguageSelectionIsAbsentFromInterviewContracts()
    {
        Assert.DoesNotContain(typeof(StartInterviewCommand).GetProperties(), property => property.Name == "InterviewLanguage");
        Assert.DoesNotContain(typeof(AiOperationContext).GetProperties(), property => property.Name == "InterviewLanguage");
    }

    private static void AssertRepair<T>(AiOperationDefinition<T> operation)
    {
        var failure = AiValidationResult<T>.Failure("test.invalid", "semantic", repairable: true);
        var repair = operation.BuildRepairInstructions(failure, operation.Instructions);

        Assert.Contains(AiLanguagePolicy.VietnameseUserFacingInstruction, repair, StringComparison.Ordinal);
    }
}
