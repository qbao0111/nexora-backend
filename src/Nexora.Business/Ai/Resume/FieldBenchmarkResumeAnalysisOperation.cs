using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class FieldBenchmarkResumeAnalysisOperation : AiOperationDefinition<ResumeAnalysisOutput>
{
    private const int InitialOutputTokens = 4_096;
    private const int TruncationRetryOutputTokens = 8_192;

    public override string Purpose => AiPurposes.ResumeAnalysis;
    public override string PromptVersion => "resume-analysis-field-benchmark-v3";
    public override string SchemaVersion => "resume-analysis-field-benchmark-v2";
    public override string RubricVersion => "analysis-field-benchmark-v2";
    public override int MaxOutputTokens => InitialOutputTokens;
    public override bool SupportsOutputTruncationRetry => true;
    public override int GetEffectiveMaxOutputTokens(int attempt, bool outputTruncationRetry) =>
        ValidateEffectiveMaxOutputTokens(attempt == 2 && outputTruncationRetry ? TruncationRetryOutputTokens : InitialOutputTokens);

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "mode": { "type": "string", "enum": ["field_benchmark"] },
            "readinessScore": { "type": "integer", "minimum": 0, "maximum": 100 },
            "summary": { "type": "string", "minLength": 1, "maxLength": 3000 },
            "strengths": { "type": "array", "minItems": 1, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "gaps": { "type": "array", "minItems": 1, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "recommendations": { "type": "array", "minItems": 1, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "sectionFeedback": { "type": "array", "minItems": 1, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "breakdown": { "type": "object", "additionalProperties": false, "properties": { "technicalFoundation": { "type": "integer", "minimum": 0, "maximum": 100 }, "projectEvidence": { "type": "integer", "minimum": 0, "maximum": 100 }, "experiencePresentation": { "type": "integer", "minimum": 0, "maximum": 100 }, "impactAchievements": { "type": "integer", "minimum": 0, "maximum": 100 }, "clarity": { "type": "integer", "minimum": 0, "maximum": 100 }, "roleAlignment": { "type": "integer", "minimum": 0, "maximum": 100 } }, "required": ["technicalFoundation", "projectEvidence", "experiencePresentation", "impactAchievements", "clarity", "roleAlignment"] }
          },
          "required": ["mode", "readinessScore", "summary", "strengths", "gaps", "recommendations", "sectionFeedback", "breakdown"]
        }
        """);

    public override string Instructions =>
        $"Benchmark the candidate's resume profile for the supplied industry, target role and seniority. Return mode='field_benchmark', a 0-100 readinessScore, concise grounded summary, 1-6 strengths, gaps and recommendations, sectionFeedback, and the six-key 0-100 breakdown. Never invent candidate achievements or qualifications. {AiLanguagePolicy.VietnameseUserFacingInstruction}";

    public override AiValidationResult<ResumeAnalysisOutput> NormalizeAndValidate(ResumeAnalysisOutput? raw, AiOperationContext context) =>
        ResumeAnalysisValidator.NormalizeFieldBenchmark(raw, context);
}
