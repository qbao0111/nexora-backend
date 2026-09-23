using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class StarEvaluateOperation : AiOperationDefinition<StarEvaluation>
{
    public override string Purpose => AiPurposes.StarEvaluate;
    public override string PromptVersion => "star-eval-v4";
    public override string SchemaVersion => "star-eval-v3";
    public override string RubricVersion => "star-rubric-v2";
    public override int MaxOutputTokens => 6_000;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "scoreScale": { "type": "string", "enum": ["0-100"] },
            "applicable": { "type": "boolean" },
            "overallScore": { "type": "integer", "minimum": 0, "maximum": 100 },
            "situation": {
              "type": "object",
              "properties": {
                "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                "detected": { "type": "boolean" },
                "evidence": { "type": "string" },
                "feedback": { "type": "string" }
              },
              "required": ["score", "detected", "evidence", "feedback"]
            },
            "task": {
              "type": "object",
              "properties": {
                "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                "detected": { "type": "boolean" },
                "evidence": { "type": "string" },
                "feedback": { "type": "string" }
              },
              "required": ["score", "detected", "evidence", "feedback"]
            },
            "action": {
              "type": "object",
              "properties": {
                "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                "detected": { "type": "boolean" },
                "evidence": { "type": "string" },
                "feedback": { "type": "string" }
              },
              "required": ["score", "detected", "evidence", "feedback"]
            },
            "result": {
              "type": "object",
              "properties": {
                "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                "detected": { "type": "boolean" },
                "evidence": { "type": "string" },
                "feedback": { "type": "string" }
              },
              "required": ["score", "detected", "evidence", "feedback"]
            },
            "missingElements": { "type": "array", "items": { "type": "string" } },
            "strengths": { "type": "array", "items": { "type": "string" } },
            "coachingTips": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["scoreScale", "applicable", "overallScore", "situation", "task", "action", "result", "missingElements", "strengths", "coachingTips"]
        }
        """);

    public override string Instructions =>
        $"""
        Evaluate the candidate's answer using the STAR methodology (Situation, Task, Action, Result).
        Set scoreScale to '0-100' and applicable to true.
        {AiLanguagePolicy.VietnameseUserFacingInstruction}
        {StarSemantics.CanonicalInstructions}
        strengths: 1-3 specific strong points.
        coachingTips: 1-3 actionable improvement tips.
        """;

    public override AiValidationResult<StarEvaluation> NormalizeAndValidate(StarEvaluation? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<StarEvaluation>.Failure("star.missing", "semantic", repairable: true);
        if (!string.Equals(raw.ScoreScale, AiOperations.ScoreScale, StringComparison.Ordinal))
            return AiValidationResult<StarEvaluation>.Failure("score.scale_invalid", "semantic", repairable: true);

        if (!raw.Applicable)
            return AiValidationResult<StarEvaluation>.Failure("star.applicability_mismatch", "semantic", repairable: true);

        var sitResult = StarComponentValidator.Validate(raw.Situation, "situation");
        if (!sitResult.IsValid)
            return AiValidationResult<StarEvaluation>.Failure(sitResult.FailureReason!, sitResult.ValidationStage!, sitResult.Repairable);

        var taskResult = StarComponentValidator.Validate(raw.Task, "task");
        if (!taskResult.IsValid)
            return AiValidationResult<StarEvaluation>.Failure(taskResult.FailureReason!, taskResult.ValidationStage!, taskResult.Repairable);

        var actResult = StarComponentValidator.Validate(raw.Action, "action");
        if (!actResult.IsValid)
            return AiValidationResult<StarEvaluation>.Failure(actResult.FailureReason!, actResult.ValidationStage!, actResult.Repairable);

        var resResult = StarComponentValidator.Validate(raw.Result, "result");
        if (!resResult.IsValid)
            return AiValidationResult<StarEvaluation>.Failure(resResult.FailureReason!, resResult.ValidationStage!, resResult.Repairable);

        var sit = sitResult.NormalizedValue!;
        var task = taskResult.NormalizedValue!;
        var act = actResult.NormalizedValue!;
        var res = resResult.NormalizedValue!;

        // Authoritative server-computed overall score: Situation 20%, Task 20%, Action 35%, Result 25%
        var overallScore = (int)Math.Round(sit.Score * 0.20 + task.Score * 0.20 + act.Score * 0.35 + res.Score * 0.25);

        // Recompute missingElements strictly from normalized server component state
        var missing = new[] { ("situation", sit), ("task", task), ("action", act), ("result", res) }
            .Where(x => !x.Item2.Detected || x.Item2.Score < 60)
            .Select(x => x.Item1)
            .ToArray();

        var strengths = raw.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var coachingTips = raw.CoachingTips?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];

        if (strengths.Length == 0)
            return AiValidationResult<StarEvaluation>.Failure("star.strengths_blank", "semantic", repairable: true);
        if (coachingTips.Length == 0)
            return AiValidationResult<StarEvaluation>.Failure("star.coaching_tips_blank", "semantic", repairable: true);

        return AiValidationResult<StarEvaluation>.Success(new StarEvaluation(
            true,
            overallScore,
            sit,
            task,
            act,
            res,
            missing,
            strengths,
            coachingTips,
            AiOperations.ScoreScale));
    }

    public override string BuildRepairInstructions(AiValidationResult<StarEvaluation> priorResult, string originalInstructions)
    {
        return $"""
            {originalInstructions}

            IMPORTANT STAR CORRECTION INSTRUCTION:
            The previous structured evaluation violated the STAR contract: '{priorResult.FailureReason}'.
            Re-evaluate the ORIGINAL candidate answer.
            Important:
            - Inspect the entire candidate answer.
            - Extract evidence before assigning detected/score.
            - Concrete technical actions (e.g. profiling, queries, coding, caching, indexing, coordinating) are Action.
            - Measurable operational outcomes (e.g. latency, recovery, runbook, metrics) are Result.
            - detected=false requires score=0 and empty evidence.
            - detected=true requires score 1-100 and direct evidence quote.
            Return a completely corrected object matching the schema.
            """;
    }
}
