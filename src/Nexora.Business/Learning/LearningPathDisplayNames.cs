using Nexora.Business.Skills;

namespace Nexora.Business.Learning;

/// <summary>
/// Learning Path-only Vietnamese labels. Unknown and technical names stay as supplied.
/// </summary>
public static class LearningPathDisplayNames
{
    private static readonly Dictionary<string, string> VietnameseNames = new(StringComparer.Ordinal)
    {
        ["action"] = "hành động",
        ["clarity"] = "độ rõ ràng",
        ["communication"] = "giao tiếp",
        ["conflict_resolution"] = "giải quyết xung đột",
        ["customer_service"] = "dịch vụ khách hàng",
        ["impact_achievements"] = "thành tích tạo ra tác động",
        ["impact_evidence"] = "minh chứng về tác động",
        ["incident_response"] = "ứng phó sự cố",
        ["leadership"] = "năng lực lãnh đạo",
        ["prioritization"] = "khả năng sắp xếp thứ tự ưu tiên",
        ["problem_solving"] = "giải quyết vấn đề",
        ["project_evidence"] = "minh chứng dự án",
        ["result"] = "kết quả",
        ["risk_management"] = "quản lý rủi ro",
        ["situation"] = "tình huống",
        ["task"] = "nhiệm vụ",
        ["teamwork"] = "làm việc nhóm",
        ["time_management"] = "quản lý thời gian"
    };

    private static readonly Dictionary<string, string> SourceNamesByVietnamese = VietnameseNames
        .ToDictionary(item => SkillProfileTaxonomy.NormalizePart(item.Value), item => HumanizeCanonicalIdentity(item.Key), StringComparer.Ordinal);

    public static string ForCompetency(string competencyCode, string? sourceName)
    {
        var separator = competencyCode.IndexOf('.', StringComparison.Ordinal);
        var identity = separator >= 0 ? competencyCode[(separator + 1)..] : competencyCode;
        return TryVietnameseName(identity, out var translated) || TryVietnameseName(sourceName, out translated)
            ? translated
            : string.IsNullOrWhiteSpace(sourceName)
                ? SkillProfileTaxonomy.DisplayName(identity)
                : sourceName.Trim();
    }

    public static string ForQualitativeLabel(string label) =>
        TryVietnameseName(label, out var translated) ? translated : label.Trim();

    public static string CanonicalSourceLabelForDisplay(string label)
    {
        var normalized = SkillProfileTaxonomy.NormalizePart(label);
        return SourceNamesByVietnamese.TryGetValue(normalized, out var sourceName) ? sourceName : label;
    }

    private static bool TryVietnameseName(string? value, out string translated)
    {
        translated = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var normalized = SkillProfileTaxonomy.NormalizePart(value);
        if (!VietnameseNames.TryGetValue(normalized, out var result)) return false;
        translated = result;
        return true;
    }

    private static string HumanizeCanonicalIdentity(string identity) =>
        string.Join(' ', identity.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
}
