using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public static class ResumeProfileValidator
{
    public static AiValidationResult<ResumeProfile> NormalizeAndValidate(ResumeProfile? raw)
    {
        if (raw is null)
            return AiValidationResult<ResumeProfile>.Failure("resume.profile_invalid", "semantic", repairable: true);

        var experiences = raw.Experiences?
            .Select(item => new ResumeExperience(
                item.Company?.Trim(),
                item.Role?.Trim(),
                item.Start?.Trim(),
                item.End?.Trim(),
                Clean(item.Highlights)))
            .Where(HasContent)
            .ToArray() ?? [];
        var education = raw.Education?
            .Select(item => new ResumeEducation(
                item.Institution?.Trim(),
                item.Degree?.Trim(),
                item.Start?.Trim(),
                item.End?.Trim(),
                Clean(item.Details)))
            .Where(HasContent)
            .ToArray() ?? [];
        var projects = raw.Projects?
            .Select(item => new ResumeProject(
                item.Name?.Trim(),
                item.Role?.Trim(),
                Clean(item.Technologies),
                Clean(item.Highlights)))
            .Where(HasContent)
            .ToArray() ?? [];
        var normalized = new ResumeProfile(
            raw.Summary?.Trim(),
            Clean(raw.Skills),
            experiences,
            education,
            projects,
            Clean(raw.Certifications),
            Clean(raw.Languages));

        var hasContent = !string.IsNullOrWhiteSpace(normalized.Summary) ||
            normalized.Skills.Count > 0 ||
            normalized.Experiences.Count > 0 ||
            normalized.Education.Count > 0 ||
            normalized.Projects.Count > 0 ||
            normalized.Certifications.Count > 0 ||
            normalized.Languages.Count > 0;
        if (!hasContent)
            return AiValidationResult<ResumeProfile>.Failure("resume.profile_invalid", "semantic", repairable: true);

        if ((normalized.Summary?.Length ?? 0) > 3_000 ||
            normalized.Skills.Count > 100 ||
            normalized.Experiences.Count > 30 ||
            normalized.Education.Count > 20 ||
            normalized.Projects.Count > 30 ||
            normalized.Certifications.Count > 50 ||
            normalized.Languages.Count > 30)
            return AiValidationResult<ResumeProfile>.Failure("resume.profile_invalid", "semantic", repairable: false);

        return AiValidationResult<ResumeProfile>.Success(normalized);
    }

    private static string[] Clean(IReadOnlyCollection<string>? values) =>
        values?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray() ?? [];

    private static bool HasContent(ResumeExperience item) =>
        !string.IsNullOrWhiteSpace(item.Company) || !string.IsNullOrWhiteSpace(item.Role) ||
        !string.IsNullOrWhiteSpace(item.Start) || !string.IsNullOrWhiteSpace(item.End) || item.Highlights.Count > 0;

    private static bool HasContent(ResumeEducation item) =>
        !string.IsNullOrWhiteSpace(item.Institution) || !string.IsNullOrWhiteSpace(item.Degree) ||
        !string.IsNullOrWhiteSpace(item.Start) || !string.IsNullOrWhiteSpace(item.End) || item.Details.Count > 0;

    private static bool HasContent(ResumeProject item) =>
        !string.IsNullOrWhiteSpace(item.Name) || !string.IsNullOrWhiteSpace(item.Role) ||
        item.Technologies.Count > 0 || item.Highlights.Count > 0;
}
