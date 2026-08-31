using Nexora.Business.Practice;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class ResumeContextBuilderTests
{
    [Fact]
    public void TaskContextsAreBoundedAndUseCompactProfileFields()
    {
        var profile = new ResumeProfile(
            "Backend developer with experience building APIs.",
            ["C#", ".NET", "PostgreSQL", "REST API"],
            [new ResumeExperience("Nexora", "Backend Developer", "2024", null, ["Built reliable APIs."])],
            [new ResumeEducation("University", "Computer Science", "2020", "2024", [])],
            [new ResumeProject("Practice platform", "Developer", ["C#"], ["Built the API."])],
            [],
            ["Vietnamese", "English"]);
        var builder = new ResumeContextBuilder();
        var raw = string.Concat(Enumerable.Repeat("Name: Candidate; phone: 0123456789; detailed resume text. ", 500));

        var profileInput = builder.BuildProfileExtractionContext(raw);
        var analysis = builder.BuildResumeAnalysisContext(profile, "Build REST APIs with C# and PostgreSQL.");
        var answer = builder.BuildAnswerEvaluationContext("Explain the API design.", "I used layered services and validation.", profile);
        var report = builder.BuildReportContext("Q: Explain the API design.\nA: I used layered services.", profile);

        Assert.True(profileInput.Length <= 20_100);
        Assert.True(analysis.Length <= 12_000);
        Assert.True(answer.Length <= 8_000);
        Assert.True(report.Length <= 16_000);
        Assert.Contains("PostgreSQL", analysis, StringComparison.Ordinal);
        Assert.DoesNotContain("0123456789", analysis, StringComparison.Ordinal);
        Assert.True(analysis.Length < profileInput.Length);
    }
}
