using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class InterviewReportOperation : AiOperationDefinition<InterviewReportOutput>
{
    public override string Purpose => AiPurposes.InterviewReport;
    public override string PromptVersion => "interview-report-v6";
    public override string SchemaVersion => "interview-report-v3";
    public override string RubricVersion => "rubric-v2";
    public override int MaxOutputTokens => 6_000;
    public override AiReasoningEffortOverride? GetRecoveryReasoningOverride(
        string modelVersion,
        AiRecoveryReason reason) =>
        reason == AiRecoveryReason.SemanticValidation &&
        modelVersion.StartsWith("deepseek:", StringComparison.OrdinalIgnoreCase)
            ? AiReasoningEffortOverride.Disabled
            : null;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "additionalProperties": false,
          "type": "object",
          "properties": {
            "scoreScale": { "type": "string", "enum": ["0-100"] },
            "scores": {
              "type": "array",
              "minItems": 4,
              "maxItems": 4,
              "items": {
                "additionalProperties": false,
                "type": "object",
                "properties": {
                  "criterion": { "type": "string", "enum": ["correctness", "structure", "completeness", "clarity"] },
                  "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                  "evidence": { "type": "string" }
                },
                "required": ["criterion", "score", "evidence"]
              }
            },
            "strengths": { "type": "array", "minItems": 0, "maxItems": 3, "items": { "type": "string", "minLength": 1, "maxLength": 500 } },
            "gaps": { "type": "array", "minItems": 1, "maxItems": 3, "items": { "type": "string", "minLength": 1, "maxLength": 500 } },
            "actionPlan": { "type": "array", "minItems": 1, "maxItems": 3, "items": { "type": "string", "minLength": 1, "maxLength": 500 } }
          },
          "required": ["scoreScale", "scores", "strengths", "gaps", "actionPlan"]
        }
        """);

    public override string Instructions =>
        $"Synthesize the interview transcript into a final coaching report. Only text after each A: is candidate evidence; Q: is context, never proof of candidate experience. For each of exactly four scores (correctness, structure, completeness, clarity; 0-100), copy a short exact phrase from an A: into evidence, including a brief answer such as 'không biết' when that is all the candidate said. Do not infer actions, skills, metrics, or achievements from the question, CV, or JD. Return 0-3 strengths supported by answer text, or [] when none; 1-3 gaps and 1-3 concrete actionPlan items. Set scoreScale to '0-100'. {AiLanguagePolicy.VietnameseUserFacingInstruction}";

    public override AiValidationResult<InterviewReportOutput> NormalizeAndValidate(InterviewReportOutput? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<InterviewReportOutput>.Failure("rubric.criteria_missing", "semantic", repairable: true);
        if (!string.Equals(raw.ScoreScale, AiOperations.ScoreScale, StringComparison.Ordinal))
            return AiValidationResult<InterviewReportOutput>.Failure("score.scale_invalid", "semantic", repairable: true);

        var rubricResult = CanonicalRubricValidator.ValidateAndNormalize(raw.Scores);
        if (!rubricResult.IsValid)
            return AiValidationResult<InterviewReportOutput>.Failure(rubricResult.FailureReason!, rubricResult.ValidationStage!, rubricResult.Repairable);

        var strengths = NormalizeReportCollection(raw.Strengths, allowEmpty: true);
        if (strengths is null)
            return AiValidationResult<InterviewReportOutput>.Failure("report.strengths_invalid", "semantic", repairable: true);
        var gaps = NormalizeReportCollection(raw.Gaps);
        if (gaps is null)
            return AiValidationResult<InterviewReportOutput>.Failure("report.gaps_invalid", "semantic", repairable: true);
        var actionPlan = NormalizeReportCollection(raw.ActionPlan);
        if (actionPlan is null)
            return AiValidationResult<InterviewReportOutput>.Failure("report.action_plan_invalid", "semantic", repairable: true);

        if (string.IsNullOrWhiteSpace(context.GroundingTranscript))
            return AiValidationResult<InterviewReportOutput>.Failure("report.grounding_context_missing", "semantic", repairable: false);

        if (rubricResult.NormalizedValue!.Any(score =>
                !AnswerCoachingValidator.IsGroundedReportEvidence(score.Evidence, context.GroundingTranscript)))
            return AiValidationResult<InterviewReportOutput>.Failure("report.rubric_evidence_ungrounded", "semantic", repairable: true);

        if (strengths.Any(strength =>
                !AnswerCoachingValidator.IsGroundedReportStrength(strength, context.GroundingTranscript)))
            return AiValidationResult<InterviewReportOutput>.Failure("report.strengths_ungrounded", "semantic", repairable: true);

        return AiValidationResult<InterviewReportOutput>.Success(
            new InterviewReportOutput(rubricResult.NormalizedValue!, strengths, gaps, actionPlan, AiOperations.ScoreScale));
    }

    public override string BuildRepairInstructions(
        AiValidationResult<InterviewReportOutput> priorResult,
        string originalInstructions)
    {
        if (priorResult.FailureReason is "report.rubric_evidence_ungrounded" or "report.strengths_ungrounded")
        {
            return $"""
                {originalInstructions}

                IMPORTANT REPORT GROUNDING CORRECTION:
                The previous report failed grounding validation: '{priorResult.FailureReason}'.
                Only the candidate's submitted answers are evidence. Interview questions, CV/resume data, profile data, and job-description data are context only and must not be cited as candidate evidence.
                Do not invent experience, actions, technologies, metrics, outcomes, or strengths. Answers such as "không biết" provide no positive experience evidence.
                Rewrite every rubric evidence item and strength so it is directly supported by words in the submitted answers, then return one complete object matching the schema.
                """;
        }

        return base.BuildRepairInstructions(priorResult, originalInstructions);
    }

    public override AiValidationResult<InterviewReportOutput>? TryRecoverTerminalValidation(
        InterviewReportOutput? raw,
        AiOperationContext context,
        AiValidationResult<InterviewReportOutput> terminalResult)
    {
        if (raw is null || terminalResult.FailureReason is not "report.strengths_ungrounded" ||
            string.IsNullOrWhiteSpace(context.GroundingTranscript))
            return null;

        var strengths = (raw.Strengths ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item) &&
                AnswerCoachingValidator.IsGroundedReportStrength(item, context.GroundingTranscript))
            .ToArray();
        return NormalizeAndValidate(raw with { Strengths = strengths }, context);
    }

    private static string[]? NormalizeReportCollection(IReadOnlyCollection<string>? values, bool allowEmpty = false)
    {
        if (values is null || values.Count > 3 || (!allowEmpty && values.Count == 0)) return null;
        var normalized = values.Select(value => value?.Trim() ?? string.Empty).ToArray();
        return normalized.Any(value => value.Length is 0 or > 500 || string.IsNullOrWhiteSpace(value))
            ? null
            : normalized;
    }
}
