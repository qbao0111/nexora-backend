namespace Nexora.Business.Skills;

public static class SkillProfileSourceTypes
{
    public const string ResumeAnalysis = "cv_analysis";
    public const string Interview = "interview";
    public const string StarAttempt = "star_attempt";
    public const string Scenario = "scenario";
}

public sealed record SkillProfileEvidence(
    string Identity,
    string Code,
    string Name,
    string Category,
    string SourceType,
    int Score,
    DateTimeOffset EvidenceAt);

public sealed record SkillProfileWeaknessSignal(
    string SourceType,
    string Label,
    DateTimeOffset LatestEvidenceAt);

public sealed record SkillProfileSource(
    string SourceType,
    int EvidenceCount,
    DateTimeOffset LatestEvidenceAt);

public sealed record SkillProfileCompetency(
    string Code,
    string Name,
    string Category,
    int Score,
    int EvidenceCount,
    DateTimeOffset LatestEvidenceAt,
    IReadOnlyCollection<SkillProfileSource> Sources);

public sealed record SkillProfileView(
    IReadOnlyCollection<SkillProfileCompetency> Competencies,
    IReadOnlyCollection<SkillProfileWeaknessSignal> WeaknessSignals);

public interface ISkillProfileService
{
    Task<SkillProfileView> GetAsync(Guid userId, CancellationToken cancellationToken);
}

public static class SkillProfileTaxonomy
{
    public static string? CreateCode(string category, string identity)
    {
        var normalizedCategory = NormalizePart(category);
        var normalizedIdentity = NormalizePart(identity);
        return normalizedCategory.Length == 0 || normalizedIdentity.Length == 0
            ? null
            : $"{normalizedCategory}.{normalizedIdentity}";
    }

    public static string NormalizePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var builder = new System.Text.StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsLetterOrDigit(character))
            {
                var previous = index > 0 ? value[index - 1] : '\0';
                var next = index + 1 < value.Length ? value[index + 1] : '\0';
                var startsWord = char.IsUpper(character) && builder.Length > 0 && builder[^1] != '_' &&
                    (char.IsLower(previous) || char.IsDigit(previous) ||
                     char.IsUpper(previous) && char.IsLower(next));
                if (startsWord) builder.Append('_');
                builder.Append(char.ToLowerInvariant(character));
            }
            else if (builder.Length > 0 && builder[^1] != '_')
            {
                builder.Append('_');
            }
        }

        return builder.ToString().Trim('_');
    }

    public static string DisplayName(string? identity)
    {
        var normalized = NormalizePart(identity);
        if (normalized.Length == 0) return string.Empty;

        return string.Join(' ', normalized.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }
}

public static class SkillProfileAggregator
{
    public static SkillProfileView Aggregate(
        IEnumerable<SkillProfileEvidence> evidence,
        IEnumerable<SkillProfileWeaknessSignal>? weaknessSignals = null)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var uniqueEvidence = new Dictionary<string, SkillProfileEvidence>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Identity) || string.IsNullOrWhiteSpace(item.Code) ||
                string.IsNullOrWhiteSpace(item.Category) || string.IsNullOrWhiteSpace(item.SourceType) ||
                item.Score is < 0 or > 100)
                continue;

            var normalizedCode = NormalizeCode(item.Code);
            if (normalizedCode is null) continue;

            var normalized = item with
            {
                Identity = item.Identity.Trim(),
                Code = normalizedCode,
                Name = string.IsNullOrWhiteSpace(item.Name)
                    ? SkillProfileTaxonomy.DisplayName(normalizedCode[(normalizedCode.IndexOf('.') + 1)..])
                    : item.Name.Trim(),
                Category = SkillProfileTaxonomy.NormalizePart(item.Category),
                SourceType = item.SourceType.Trim().ToLowerInvariant()
            };
            if (normalized.Category.Length == 0 || normalized.SourceType.Length == 0) continue;

            if (!uniqueEvidence.TryGetValue(normalized.Identity, out var prior) || IsLater(normalized, prior))
                uniqueEvidence[normalized.Identity] = normalized;
        }

        var competencies = uniqueEvidence.Values
            .GroupBy(item => item.Code, StringComparer.Ordinal)
            .Select(group => new SkillProfileCompetency(
                group.Key,
                group.Select(item => item.Name).OrderBy(item => item, StringComparer.Ordinal).First(),
                group.Select(item => item.Category).OrderBy(item => item, StringComparer.Ordinal).First(),
                (int)Math.Round(group.Average(item => item.Score), MidpointRounding.AwayFromZero),
                group.Count(),
                group.Max(item => item.EvidenceAt),
                group.GroupBy(item => item.SourceType, StringComparer.Ordinal)
                    .Select(source => new SkillProfileSource(source.Key, source.Count(), source.Max(item => item.EvidenceAt)))
                    .OrderBy(source => source.SourceType, StringComparer.Ordinal)
                    .ToArray()))
            .OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();

        var normalizedWeaknesses = (weaknessSignals ?? [])
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.SourceType) && !string.IsNullOrWhiteSpace(item.Label))
            .Select(item => item with
            {
                SourceType = item.SourceType.Trim().ToLowerInvariant(),
                Label = item.Label.Trim()
            })
            .GroupBy(item => $"{item.SourceType}\u001f{item.Label.ToLowerInvariant()}", StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(item => item.LatestEvidenceAt)
                .ThenBy(item => item.Label, StringComparer.Ordinal)
                .First())
            .OrderBy(item => item.SourceType, StringComparer.Ordinal)
            .ThenBy(item => item.Label, StringComparer.Ordinal)
            .ToArray();

        return new SkillProfileView(competencies, normalizedWeaknesses);
    }

    private static string? NormalizeCode(string code)
    {
        var separator = code.IndexOf('.', StringComparison.Ordinal);
        if (separator <= 0 || separator == code.Length - 1) return null;

        var category = SkillProfileTaxonomy.NormalizePart(code[..separator]);
        var identity = SkillProfileTaxonomy.NormalizePart(code[(separator + 1)..]);
        return category.Length == 0 || identity.Length == 0 ? null : $"{category}.{identity}";
    }

    private static bool IsLater(SkillProfileEvidence candidate, SkillProfileEvidence prior)
    {
        var timestamp = candidate.EvidenceAt.CompareTo(prior.EvidenceAt);
        if (timestamp != 0) return timestamp > 0;

        var code = string.CompareOrdinal(candidate.Code, prior.Code);
        if (code != 0) return code < 0;

        var source = string.CompareOrdinal(candidate.SourceType, prior.SourceType);
        if (source != 0) return source < 0;

        var category = string.CompareOrdinal(candidate.Category, prior.Category);
        if (category != 0) return category < 0;

        var name = string.CompareOrdinal(candidate.Name, prior.Name);
        if (name != 0) return name < 0;

        return candidate.Score < prior.Score;
    }
}
