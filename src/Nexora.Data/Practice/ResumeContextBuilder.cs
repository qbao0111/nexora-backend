using System.Text;
using Nexora.Business.Practice;

namespace Nexora.Data.Practice;

public sealed class ResumeContextBuilder : IResumeContextBuilder
{
    private const int ProfileInputLimit = 20_000;
    private const int AnalysisContextLimit = 12_000;
    private const int InterviewContextLimit = 9_000;
    private const int AnswerContextLimit = 8_000;
    private const int ReportContextLimit = 16_000;

    public string BuildProfileExtractionContext(string rawExtractedText) =>
        $"<resume-text>\n{Bound(rawExtractedText, ProfileInputLimit)}\n</resume-text>";

    public string BuildResumeAnalysisContext(ResumeProfile profile, string jobDescription)
    {
        var builder = new StringBuilder();
        AppendProfile(builder, profile, includeDetails: true);
        Append(builder, "target-job", jobDescription, 8_000);
        return Bound(builder.ToString(), AnalysisContextLimit);
    }

    public string BuildInterviewQuestionContext(
        string role, string seniority, string? jobDescription, ResumeProfile? profile)
    {
        var builder = new StringBuilder();
        Append(builder, "role", role, 160);
        Append(builder, "seniority", seniority, 80);
        Append(builder, "job-description", jobDescription, 5_000);
        if (profile is not null) AppendProfile(builder, profile, includeDetails: false);
        return Bound(builder.ToString(), InterviewContextLimit);
    }

    public string BuildAnswerEvaluationContext(string question, string answer, ResumeProfile? profile)
    {
        var builder = new StringBuilder();
        Append(builder, "question", question, 2_000);
        Append(builder, "answer", answer, 12_000);
        if (profile is not null) AppendProfile(builder, profile, includeDetails: false);
        return Bound(builder.ToString(), AnswerContextLimit);
    }

    public string BuildReportContext(string transcript, ResumeProfile? profile)
    {
        var builder = new StringBuilder();
        Append(builder, "transcript", transcript, 14_000);
        if (profile is not null) AppendProfile(builder, profile, includeDetails: false);
        return Bound(builder.ToString(), ReportContextLimit);
    }

    private static void AppendProfile(StringBuilder builder, ResumeProfile profile, bool includeDetails)
    {
        Append(builder, "summary", profile.Summary, 1_500);
        AppendCollection(builder, "skills", profile.Skills, 40, 120);
        AppendCollection(builder, "certifications", profile.Certifications, 20, 160);
        AppendCollection(builder, "languages", profile.Languages, 20, 100);

        var experiences = profile.Experiences ?? [];
        builder.AppendLine("experiences:");
        foreach (var experience in experiences.Take(includeDetails ? 6 : 3))
        {
            builder.Append("- ");
            AppendInline(builder, experience.Role, 160);
            AppendInline(builder, experience.Company, 180);
            AppendInline(builder, experience.Start, 40);
            AppendInline(builder, experience.End, 40);
            builder.AppendLine();
            AppendCollection(builder, "  highlights", experience.Highlights, includeDetails ? 4 : 2, 280);
        }

        if (!includeDetails) return;

        builder.AppendLine("education:");
        foreach (var education in (profile.Education ?? []).Take(4))
        {
            builder.Append("- ");
            AppendInline(builder, education.Degree, 160);
            AppendInline(builder, education.Institution, 200);
            AppendInline(builder, education.Start, 40);
            AppendInline(builder, education.End, 40);
            builder.AppendLine();
            AppendCollection(builder, "  details", education.Details, 3, 220);
        }

        builder.AppendLine("projects:");
        foreach (var project in (profile.Projects ?? []).Take(6))
        {
            builder.Append("- ");
            AppendInline(builder, project.Name, 180);
            AppendInline(builder, project.Role, 160);
            builder.AppendLine();
            AppendCollection(builder, "  technologies", project.Technologies, 12, 100);
            AppendCollection(builder, "  highlights", project.Highlights, 4, 280);
        }
    }

    private static void AppendCollection<T>(StringBuilder builder, string label, IEnumerable<T>? values, int maxItems, int maxItemLength)
    {
        var items = values?.Select(value => value?.ToString()).Where(value => !string.IsNullOrWhiteSpace(value))
            .Take(maxItems).Select(value => Bound(value!, maxItemLength)).ToArray() ?? [];
        if (items.Length == 0) return;
        builder.Append(label).Append(": ").AppendLine(string.Join("; ", items));
    }

    private static void Append(StringBuilder builder, string label, string? value, int maxLength)
    {
        var bounded = Bound(value, maxLength);
        if (bounded.Length > 0) builder.Append(label).Append(": ").AppendLine(bounded);
    }

    private static void AppendInline(StringBuilder builder, string? value, int maxLength)
    {
        var bounded = Bound(value, maxLength);
        if (bounded.Length > 0) builder.Append(bounded).Append(" | ");
    }

    private static string Bound(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim()[..Math.Min(value.Trim().Length, maxLength)];
}
