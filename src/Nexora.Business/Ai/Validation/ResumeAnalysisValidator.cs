using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public static class ResumeAnalysisValidator
{
    private const int MaximumSummaryLength = 3_000;
    private const int MaximumItemLength = 1_000;
    private const int MaximumListItems = 6;

    public static AiValidationResult<ResumeAnalysisOutput> NormalizeJobTargeted(
        ResumeAnalysisOutput? raw,
        AiOperationContext context)
    {
        if (raw is null)
            return Invalid("resume.analysis_invalid");
        if (!TryGetMode(context, out var requestedMode))
            return Invalid("resume.analysis_mode_required");
        if (!string.Equals(requestedMode, ResumeAnalysisModes.JobTargeted, StringComparison.Ordinal))
            return Invalid("resume.analysis_mode_invalid");

        if (!string.Equals(raw.Mode, ResumeAnalysisModes.JobTargeted, StringComparison.Ordinal))
            return Invalid("resume.analysis_mode_invalid");
        if (raw.MatchScore is null or < 0 or > 100)
            return Invalid("resume.match_score_invalid");
        if (raw.ReadinessScore is not null)
            return Invalid("resume.analysis_mode_mismatch");
        if (!TryNormalizeRequiredText(raw.Summary, MaximumSummaryLength, "resume.summary_blank", out var summary))
            return Invalid("resume.summary_blank");
        if (!TryNormalizeList(raw.MatchedKeywordsOrSkills, allowEmpty: true, out var matched))
            return Invalid("resume.matched_keywords_blank");
        if (!TryNormalizeList(raw.MissingKeywordsOrSkills, allowEmpty: true, out var missing))
            return Invalid("resume.missing_keywords_blank");
        if (!TryNormalizeList(raw.Strengths, allowEmpty: false, out var strengths))
            return Invalid("resume.strengths_blank");
        if (!TryNormalizeList(raw.Gaps, allowEmpty: false, out var gaps))
            return Invalid("resume.gaps_blank");
        if (!TryNormalizeList(raw.Recommendations, allowEmpty: false, out var recommendations))
            return Invalid("resume.recommendations_blank");
        if (!TryNormalizeList(raw.SectionFeedback, allowEmpty: false, out var sectionFeedback))
            return Invalid("resume.section_feedback_invalid");
        if (!TryNormalizeBreakdown(raw.Breakdown, [
                "technicalSkillMatch", "experienceRelevance", "impactEvidence", "clarity", "structure"
            ], out var breakdown))
            return Invalid("resume.breakdown_invalid");

        return AiValidationResult<ResumeAnalysisOutput>.Success(new ResumeAnalysisOutput(
            strengths,
            gaps,
            recommendations,
            raw.MatchScore,
            null,
            summary,
            matched,
            missing,
            sectionFeedback,
            breakdown,
            ResumeAnalysisModes.JobTargeted));
    }

    public static AiValidationResult<ResumeAnalysisOutput> NormalizeFieldBenchmark(
        ResumeAnalysisOutput? raw,
        AiOperationContext context)
    {
        if (raw is null)
            return Invalid("resume.analysis_invalid");
        if (!TryGetMode(context, out var requestedMode))
            return Invalid("resume.analysis_mode_required");
        if (!string.Equals(requestedMode, ResumeAnalysisModes.FieldBenchmark, StringComparison.Ordinal))
            return Invalid("resume.analysis_mode_invalid");
        if (!string.Equals(raw.Mode, ResumeAnalysisModes.FieldBenchmark, StringComparison.Ordinal))
            return Invalid("resume.analysis_mode_invalid");
        if (raw.ReadinessScore is null or < 0 or > 100)
            return Invalid("resume.readiness_score_invalid");
        if (raw.MatchScore is not null || raw.MatchedKeywordsOrSkills is not null || raw.MissingKeywordsOrSkills is not null)
            return Invalid("resume.analysis_mode_mismatch");
        if (!TryNormalizeRequiredText(raw.Summary, MaximumSummaryLength, "resume.summary_blank", out var summary))
            return Invalid("resume.summary_blank");
        if (!TryNormalizeList(raw.Strengths, allowEmpty: false, out var strengths))
            return Invalid("resume.strengths_blank");
        if (!TryNormalizeList(raw.Gaps, allowEmpty: false, out var gaps))
            return Invalid("resume.gaps_blank");
        if (!TryNormalizeList(raw.Recommendations, allowEmpty: false, out var recommendations))
            return Invalid("resume.recommendations_blank");
        if (!TryNormalizeList(raw.SectionFeedback, allowEmpty: false, out var sectionFeedback))
            return Invalid("resume.section_feedback_invalid");
        if (!TryNormalizeBreakdown(raw.Breakdown, [
                "technicalFoundation", "projectEvidence", "experiencePresentation", "impactAchievements", "clarity", "roleAlignment"
            ], out var breakdown))
            return Invalid("resume.breakdown_invalid");

        return AiValidationResult<ResumeAnalysisOutput>.Success(new ResumeAnalysisOutput(
            strengths,
            gaps,
            recommendations,
            null,
            raw.ReadinessScore,
            summary,
            null,
            null,
            sectionFeedback,
            breakdown,
            ResumeAnalysisModes.FieldBenchmark));
    }

    private static bool TryGetMode(AiOperationContext context, out string mode)
    {
        mode = string.Empty;
        if (context.Metadata is null || !context.Metadata.TryGetValue(ResumeAnalysisMetadata.Mode, out var candidate) || string.IsNullOrWhiteSpace(candidate))
            return false;

        mode = candidate;
        return true;
    }

    private static bool TryNormalizeRequiredText(string? value, int maxLength, string _, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length > 0 && normalized.Length <= maxLength;
    }

    private static bool TryNormalizeList(
        IReadOnlyCollection<string>? values,
        bool allowEmpty,
        out IReadOnlyCollection<string> normalized)
    {
        if (values is null || values.Count > MaximumListItems || (!allowEmpty && values.Count == 0) || values.Any(value => string.IsNullOrWhiteSpace(value)))
        {
            normalized = [];
            return false;
        }

        normalized = values.Select(value => value.Trim()).ToArray();
        return normalized.All(value => value.Length <= MaximumItemLength);
    }

    private static bool TryNormalizeBreakdown(
        IReadOnlyDictionary<string, int>? values,
        IReadOnlyCollection<string> requiredKeys,
        out IReadOnlyDictionary<string, int> normalized)
    {
        var normalizedValues = new Dictionary<string, int>(StringComparer.Ordinal);
        normalized = normalizedValues;
        if (values is null || values.Count != requiredKeys.Count)
            return false;

        var required = requiredKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = values
            .Select(item => (Key: item.Key.Trim(), item.Value))
            .Where(item => item.Key.Length > 0)
            .ToArray();
        if (candidates.Length != required.Count ||
            candidates.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != required.Count ||
            candidates.Any(item => !required.Contains(item.Key)))
            return false;

        foreach (var key in requiredKeys)
        {
            var match = candidates.Single(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value is < 0 or > 100)
                return false;
            normalizedValues[key] = match.Value;
        }

        return normalizedValues.Count == requiredKeys.Count;
    }

    private static AiValidationResult<ResumeAnalysisOutput> Invalid(string reason) =>
        AiValidationResult<ResumeAnalysisOutput>.Failure(reason, "semantic", repairable: true);
}
