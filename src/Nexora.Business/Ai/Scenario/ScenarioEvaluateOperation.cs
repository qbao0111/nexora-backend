using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class ScenarioEvaluateOperation : AiOperationDefinition<ScenarioEvaluationResult>
{
    private const int OutputTokenBudget = 4_000;

    public override string Purpose => AiPurposes.ScenarioEvaluate;
    public override string PromptVersion => "scenario-eval-v3";
    public override string SchemaVersion => "scenario-eval-v2";
    public override string RubricVersion => "scenario-rubric-v2";
    public override int MaxOutputTokens => OutputTokenBudget;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "scoreScale": { "type": "string", "enum": ["0-100"] },
            "overallScore": { "type": "integer", "minimum": 0, "maximum": 100 },
            "dimensions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "criterion": { "type": "string" },
                  "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                  "evidence": { "type": "string" },
                  "feedback": { "type": "string" }
                },
                "required": ["criterion", "score", "evidence", "feedback"]
              }
            },
            "strengths": { "type": "array", "items": { "type": "string" } },
            "gaps": { "type": "array", "items": { "type": "string" } },
            "recommendedApproach": { "type": "array", "items": { "type": "string" } },
            "feedback": { "type": "string" }
          },
          "required": ["scoreScale", "overallScore", "dimensions", "strengths", "gaps", "recommendedApproach", "feedback"]
        }
        """);

    public override string Instructions =>
        $"Evaluate the candidate's scenario response against the scenario requirements, difficulty, and target competency. Set scoreScale to '0-100'. Return overallScore (integer 0-100), dimensions array (2 to 4 dimensions; each with criterion, score 0-100, non-empty evidence quote from answer, and actionable feedback), strengths (1-3 items), gaps (1-3 items), recommendedApproach (1-3 actionable steps), and overall feedback summary. {AiLanguagePolicy.VietnameseUserFacingInstruction}";

    public override AiValidationResult<ScenarioEvaluationResult> NormalizeAndValidate(ScenarioEvaluationResult? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.dimensions_missing", "semantic", repairable: true);
        if (!string.Equals(raw.ScoreScale, AiOperations.ScoreScale, StringComparison.Ordinal))
            return AiValidationResult<ScenarioEvaluationResult>.Failure("score.scale_invalid", "semantic", repairable: true);

        if (raw.OverallScore is < 0 or > 100)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.score_out_of_range", "semantic", repairable: true);

        var dimensions = raw.Dimensions ?? [];
        var normalizedDimensions = new List<ScenarioDimensionEvaluation>(dimensions.Count);
        foreach (var d in dimensions)
        {
            if (d is null || string.IsNullOrWhiteSpace(d.Criterion))
                return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.criterion_blank", "semantic", repairable: true);
            if (d.Score is < 0 or > 100)
                return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.dimension_score_out_of_range", "semantic", repairable: true);
            if (string.IsNullOrWhiteSpace(d.Evidence))
                return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.evidence_blank", "semantic", repairable: true);
            if (string.IsNullOrWhiteSpace(d.Feedback))
                return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.feedback_blank", "semantic", repairable: true);

            var criterion = d.Criterion.Trim();
            normalizedDimensions.Add(new ScenarioDimensionEvaluation(criterion, d.Score, d.Evidence.Trim(), d.Feedback.Trim()));
        }

        if (normalizedDimensions.Count is < 2 or > 4)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.dimensions_count_invalid", "semantic", repairable: true);

        var strengths = raw.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var gaps = raw.Gaps?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var approach = raw.RecommendedApproach?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        if (strengths.Length == 0)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.strengths_blank", "semantic", repairable: true);
        if (gaps.Length == 0)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.gaps_blank", "semantic", repairable: true);
        if (approach.Length == 0)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.recommended_approach_blank", "semantic", repairable: true);
        if (string.IsNullOrWhiteSpace(raw.Feedback))
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.overall_feedback_blank", "semantic", repairable: true);

        return AiValidationResult<ScenarioEvaluationResult>.Success(
            new ScenarioEvaluationResult(raw.OverallScore, normalizedDimensions, strengths, gaps, approach, raw.Feedback.Trim(), AiOperations.ScoreScale));
    }
}
