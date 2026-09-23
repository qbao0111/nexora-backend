using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class ResumeAnalysisOperation : AiOperationDefinition<ResumeAnalysisOutput>
{
    private const int InitialOutputTokens = 4_096;
    private const int TruncationRetryOutputTokens = 8_192;

    public override string Purpose => AiPurposes.ResumeAnalysis;
    public override string PromptVersion => "resume-analysis-job-targeted-v3";
    public override string SchemaVersion => "resume-analysis-job-targeted-v2";
    public override string RubricVersion => "analysis-job-targeted-v2";
    public override int MaxOutputTokens => InitialOutputTokens;
    public override bool SupportsOutputTruncationRetry => true;
    public override int GetEffectiveMaxOutputTokens(int attempt, bool outputTruncationRetry) =>
        ValidateEffectiveMaxOutputTokens(attempt == 2 && outputTruncationRetry ? TruncationRetryOutputTokens : InitialOutputTokens);

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "properties": {
            "mode": { "type": "string", "enum": ["job_targeted"] },
            "matchScore": { "type": "integer", "minimum": 0, "maximum": 100 },
            "summary": { "type": "string", "minLength": 1, "maxLength": 3000 },
            "matchedKeywordsOrSkills": { "type": "array", "minItems": 0, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "missingKeywordsOrSkills": { "type": "array", "minItems": 0, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "strengths": { "type": "array", "minItems": 1, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "gaps": { "type": "array", "minItems": 1, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "recommendations": { "type": "array", "minItems": 1, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "sectionFeedback": { "type": "array", "minItems": 1, "maxItems": 6, "items": { "type": "string", "maxLength": 1000 } },
            "breakdown": { "type": "object", "additionalProperties": false, "properties": { "technicalSkillMatch": { "type": "integer", "minimum": 0, "maximum": 100 }, "experienceRelevance": { "type": "integer", "minimum": 0, "maximum": 100 }, "impactEvidence": { "type": "integer", "minimum": 0, "maximum": 100 }, "clarity": { "type": "integer", "minimum": 0, "maximum": 100 }, "structure": { "type": "integer", "minimum": 0, "maximum": 100 } }, "required": ["technicalSkillMatch", "experienceRelevance", "impactEvidence", "clarity", "structure"] }
          },
          "required": ["mode", "matchScore", "summary", "matchedKeywordsOrSkills", "missingKeywordsOrSkills", "strengths", "gaps", "recommendations", "sectionFeedback", "breakdown"]
        }
        """);

    public override string Instructions =>
        $"Analyze the candidate's resume profile against the target job description. Return mode='job_targeted', a 0-100 matchScore, a concise grounded summary, matched and missing skills (which may be empty when none are evidenced), 1-6 strengths, gaps and recommendations, sectionFeedback, and the five-key 0-100 breakdown. Never invent candidate achievements or qualifications. {AiLanguagePolicy.VietnameseUserFacingInstruction}";

    public override AiValidationResult<ResumeAnalysisOutput> NormalizeAndValidate(ResumeAnalysisOutput? raw, AiOperationContext context) =>
        ResumeAnalysisValidator.NormalizeJobTargeted(raw, context);
}
