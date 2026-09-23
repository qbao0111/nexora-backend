using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class InterviewFollowupOperation : AiOperationDefinition<GeneratedQuestion>
{
    public override string Purpose => AiPurposes.InterviewFollowup;
    public override string PromptVersion => "interview-followup-v4";
    public override string SchemaVersion => "interview-followup-v2";
    public override string RubricVersion => "rubric-v2";
    public override int MaxOutputTokens => 500;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "content": { "type": "string" }
          },
          "required": ["content"]
        }
        """);

    public override string Instructions =>
        $"Generate one concise, natural follow-up interview question based on the candidate's previous answer and context. If STAR missing elements or coaching tips are provided, probe for the missing details (such as specific actions taken, technical decisions, or measurable impact) without mechanically using the word 'STAR'. The question must be under 2,000 characters. {AiLanguagePolicy.VietnameseUserFacingInstruction}";

    public override AiValidationResult<GeneratedQuestion> NormalizeAndValidate(GeneratedQuestion? raw, AiOperationContext context)
    {
        if (raw is null || string.IsNullOrWhiteSpace(raw.Content))
            return AiValidationResult<GeneratedQuestion>.Failure("question.blank", "semantic", repairable: true);

        var trimmed = raw.Content.Trim();
        if (trimmed.Length > 2_000)
            return AiValidationResult<GeneratedQuestion>.Failure("question.too_long", "semantic", repairable: true);

        return AiValidationResult<GeneratedQuestion>.Success(new GeneratedQuestion(trimmed));
    }
}
