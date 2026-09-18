using System.Globalization;
using System.Text;
using Nexora.Business.Skills;

namespace Nexora.Business.Learning;

/// <summary>
/// Collapses labels only when their canonical topic identities match. This
/// intentionally prefers missed merges over merging distinct learning topics.
/// </summary>
public static class LearningPathQualitativeSignalDeduper
{
    private static readonly HashSet<string> GenericTokens = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "at", "cai", "can", "chua", "co", "cua", "demonstrate", "demonstrated",
        "experience", "experiences", "experienced", "for", "hien", "improve", "improved", "improvement",
        "in", "implement", "implementation", "implementing", "knowledge", "khai", "kien", "kinh", "ky",
        "lack", "lacks", "lacking", "missing", "nang", "need", "needed", "needs", "nghiem", "of", "show",
        "shown", "shows", "skill", "skills", "su", "the", "thieu", "thien", "to", "trien", "using", "va",
        "voi", "with"
    };

    public static IReadOnlyCollection<DeduplicatedQualitativeSignal> Deduplicate(
        IEnumerable<SkillProfileWeaknessSignal> signals)
    {
        ArgumentNullException.ThrowIfNull(signals);

        var exactGroups = signals
            .Where(signal => signal is not null &&
                             string.Equals(signal.SourceType?.Trim(), SkillProfileSourceTypes.ResumeAnalysis, StringComparison.OrdinalIgnoreCase) &&
                             !string.IsNullOrWhiteSpace(signal.Label))
            .Select(CreateCandidate)
            .Where(candidate => candidate is not null)
            .Select(candidate => candidate!)
            .GroupBy(candidate => candidate.ExactKey, StringComparer.Ordinal)
            .Select(group => Merge(group))
            .OrderBy(candidate => candidate.StableTopicIdentity, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ExactKey, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Signal.Label, StringComparer.Ordinal)
            .ToArray();

        var clusters = new List<List<Candidate>>();
        foreach (var candidate in exactGroups)
        {
            var cluster = clusters.FirstOrDefault(existing => existing.All(item => IsSameTopic(candidate, item)));
            if (cluster is null)
            {
                clusters.Add([candidate]);
                continue;
            }

            cluster.Add(candidate);
        }

        return clusters
            .Select(Merge)
            .OrderByDescending(candidate => candidate.Signal.LatestEvidenceAt)
            .ThenBy(candidate => candidate.StableTopicIdentity, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ExactKey, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.Signal.Label, StringComparer.Ordinal)
            .Select(candidate => new DeduplicatedQualitativeSignal(candidate.Signal, candidate.StableTopicIdentity))
            .ToArray();
    }

    /// <summary>
    /// Produces the same non-empty identity for any labels eligible to merge.
    /// Generic-only labels use their full normalized token set as a fallback.
    /// </summary>
    public static string StableTopicIdentityForLabel(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var tokens = NormalizeTokens(label);
        if (tokens.Length == 0)
            return $"fallback:{NormalizeFallbackLabel(label)}";

        var significantTokens = tokens.Where(token => !GenericTokens.Contains(token)).ToArray();
        return significantTokens.Length > 0
            ? $"topic:{Join(significantTokens)}"
            : $"generic:{Join(tokens)}";
    }

    private static Candidate? CreateCandidate(SkillProfileWeaknessSignal signal)
    {
        var label = signal.Label.Trim();
        var tokens = NormalizeTokens(label);
        var exactKey = tokens.Length > 0 ? Join(tokens) : NormalizeFallbackLabel(label);
        var significantTokens = tokens.Where(token => !GenericTokens.Contains(token)).ToArray();
        var stableTopicIdentity = significantTokens.Length > 0
            ? $"topic:{Join(significantTokens)}"
            : tokens.Length > 0
                ? $"generic:{Join(tokens)}"
                : $"fallback:{exactKey}";

        return new Candidate(
            signal with
            {
                SourceType = SkillProfileSourceTypes.ResumeAnalysis,
                Label = label
            },
            exactKey,
            stableTopicIdentity,
            significantTokens);
    }

    private static Candidate Merge(IEnumerable<Candidate> candidates)
    {
        var items = candidates.ToArray();
        var representative = items
            .OrderByDescending(item => item.SignificantTokens.Length)
            .ThenBy(item => item.Signal.Label.Length)
            .ThenBy(item => item.ExactKey, StringComparer.Ordinal)
            .ThenBy(item => item.Signal.Label, StringComparer.Ordinal)
            .First();

        return representative with
        {
            Signal = representative.Signal with
            {
                LatestEvidenceAt = items.Max(item => item.Signal.LatestEvidenceAt)
            }
        };
    }

    private static bool IsSameTopic(Candidate first, Candidate second) =>
        string.Equals(first.StableTopicIdentity, second.StableTopicIdentity, StringComparison.Ordinal);

    private static string[] NormalizeTokens(string value)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();

        void AddToken()
        {
            if (token.Length == 0) return;
            tokens.Add(token.ToString());
            token.Clear();
        }

        foreach (var rune in value.Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or
                UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
                continue;

            if (Rune.IsLetterOrDigit(rune))
                token.Append(Rune.ToLowerInvariant(rune).ToString());
            else if (token.Length > 0 && rune.Value is '+' or '#')
                token.Append(rune.ToString());
            else
                AddToken();
        }

        AddToken();
        return tokens.Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeFallbackLabel(string label) =>
        string.Join(' ', label.Normalize(NormalizationForm.FormKC).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static string Join(IEnumerable<string> tokens) => string.Join('\u001f', tokens);

    private sealed record Candidate(
        SkillProfileWeaknessSignal Signal,
        string ExactKey,
        string StableTopicIdentity,
        string[] SignificantTokens);
}

public sealed record DeduplicatedQualitativeSignal(
    SkillProfileWeaknessSignal Signal,
    string StableTopicIdentity);
