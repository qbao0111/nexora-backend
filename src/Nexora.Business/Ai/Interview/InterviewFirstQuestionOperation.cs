using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class InterviewFirstQuestionOperation : AiOperationDefinition<GeneratedQuestion>
{
    public override string Purpose => AiPurposes.InterviewFirstQuestion;
    public override string PromptVersion => "interview-first-question-v5";
    public override string SchemaVersion => "interview-first-question-v2";
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
        $"Generate one concise, realistic interview question for the supplied role and seniority. The server-owned question-topic in the context is authoritative whenever present; generate only that topic and never infer semantic topic from interview-type or question-sequence. For self_introduction, ask for the candidate's background and relevant experience. For behavioral_star, invite one real-world situation and the candidate's actions and result. For motivation_role_fit, ask about motivation and fit for the role. For other topics, follow the explicit topic and supplied role context. The question must be under 2,000 characters. Do not mention or explain the STAR acronym. {AiLanguagePolicy.VietnameseUserFacingInstruction}";

    public override AiValidationResult<GeneratedQuestion> NormalizeAndValidate(GeneratedQuestion? raw, AiOperationContext context)
    {
        if (raw is null || string.IsNullOrWhiteSpace(raw.Content))
            return AiValidationResult<GeneratedQuestion>.Failure("question.blank", "semantic", repairable: true);

        var trimmed = raw.Content.Trim();
        if (trimmed.Length > 2_000)
            return AiValidationResult<GeneratedQuestion>.Failure("question.too_long", "semantic", repairable: true);

        if (context.PreviousQuestions?.Any(previous => IsObviousDuplicate(trimmed, previous)) == true)
            return AiValidationResult<GeneratedQuestion>.Failure("question.duplicate", "semantic", repairable: true);

        return AiValidationResult<GeneratedQuestion>.Success(new GeneratedQuestion(trimmed));
    }

    private static bool IsObviousDuplicate(string candidate, string previous)
    {
        static HashSet<string> Words(string value) =>
            new string(value.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray())
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word is not ("hãy" or "bạn" or "vui" or "lòng" or "cho" or "biết" or "trình" or "bày" or "về"))
                .ToHashSet(StringComparer.Ordinal);

        var left = Words(candidate);
        var right = Words(previous);
        if (left.Count == 0 || right.Count == 0) return false;
        var overlap = left.Intersect(right).Count();
        return overlap >= Math.Min(left.Count, right.Count) * 0.8 &&
            Math.Max(left.Count, right.Count) <= Math.Min(left.Count, right.Count) + 3;
    }
}
