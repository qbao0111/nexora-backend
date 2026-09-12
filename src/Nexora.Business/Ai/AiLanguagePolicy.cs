namespace Nexora.Business.Ai;

public static class AiLanguagePolicy
{
    public const string VietnameseUserFacingInstruction =
        "Always write all user-facing natural-language content in Vietnamese. Do not infer output language from the role, CV, job description, candidate answer, company, industry, scenario, source document, transcript, or any other supplied context. Keep technical identifiers, technology names, proper nouns, acronyms, programming languages, and code terms unchanged when appropriate. Keep machine field names, enums, statuses, purpose names, schema versions, score scales, rubric keys, and competency codes canonical.";
}
