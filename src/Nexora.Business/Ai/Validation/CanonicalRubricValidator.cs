namespace Nexora.Business.Ai;

public static class CanonicalRubricValidator
{
    public static readonly string[] RequiredCriteria = ["correctness", "structure", "completeness", "clarity"];

    public static AiValidationResult<IReadOnlyCollection<RubricScore>> ValidateAndNormalize(
        IReadOnlyCollection<RubricScore>? rawScores)
    {
        if (rawScores is null || rawScores.Count == 0)
            return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.criteria_missing", "semantic", repairable: true);

        var normalized = new List<RubricScore>(rawScores.Count);
        foreach (var s in rawScores)
        {
            if (s is null)
                return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.item_invalid", "semantic", repairable: true);

            var criterion = s.Criterion?.Trim().ToLowerInvariant() ?? string.Empty;
            var evidence = s.Evidence?.Trim() ?? string.Empty;
            normalized.Add(new RubricScore(criterion, s.Score, evidence));
        }

        if (normalized.Count < 4)
            return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.criteria_missing", "semantic", repairable: true);

        if (normalized.Count > 4)
            return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.criteria_extra", "semantic", repairable: true);

        if (normalized.Any(s => !RequiredCriteria.Contains(s.Criterion)))
            return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.criteria_extra", "semantic", repairable: true);

        if (normalized.Select(s => s.Criterion).Distinct(StringComparer.Ordinal).Count() != 4)
            return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.criteria_duplicate", "semantic", repairable: true);

        if (RequiredCriteria.Any(req => !normalized.Any(s => s.Criterion == req)))
            return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.criteria_missing", "semantic", repairable: true);

        if (normalized.Any(s => s.Score is < 0 or > 100))
            return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.score_out_of_range", "semantic", repairable: true);

        if (normalized.Any(s => string.IsNullOrWhiteSpace(s.Evidence)))
            return AiValidationResult<IReadOnlyCollection<RubricScore>>.Failure("rubric.evidence_blank", "semantic", repairable: true);

        return AiValidationResult<IReadOnlyCollection<RubricScore>>.Success(normalized);
    }
}
