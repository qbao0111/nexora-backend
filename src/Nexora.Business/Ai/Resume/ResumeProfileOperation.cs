using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public sealed class ResumeProfileOperation : AiOperationDefinition<ResumeProfile>
{
    public override string Purpose => AiPurposes.ResumeProfile;
    public override string PromptVersion => "resume-profile-v3";
    public override string SchemaVersion => "resume-profile-v2";
    public override string RubricVersion => "profile-v2";
    public override int MaxOutputTokens => 3_000;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string", "nullable": true },
            "skills": { "type": "array", "items": { "type": "string" } },
            "experiences": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "company": { "type": "string", "nullable": true },
                  "role": { "type": "string", "nullable": true },
                  "start": { "type": "string", "nullable": true },
                  "end": { "type": "string", "nullable": true },
                  "highlights": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "education": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "institution": { "type": "string", "nullable": true },
                  "degree": { "type": "string", "nullable": true },
                  "start": { "type": "string", "nullable": true },
                  "end": { "type": "string", "nullable": true },
                  "details": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "projects": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string", "nullable": true },
                  "role": { "type": "string", "nullable": true },
                  "technologies": { "type": "array", "items": { "type": "string" } },
                  "highlights": { "type": "array", "items": { "type": "string" } }
                }
              }
            },
            "certifications": { "type": "array", "items": { "type": "string" } },
            "languages": { "type": "array", "items": { "type": "string" } }
          }
        }
        """);

    public override string Instructions =>
        $"Extract a faithful, structured resume profile strictly from the provided resume text. Never infer or fabricate details. For any missing section, return an empty array. Do not fail if optional sections are absent. {AiLanguagePolicy.VietnameseUserFacingInstruction}";

    public override AiValidationResult<ResumeProfile> NormalizeAndValidate(ResumeProfile? raw, AiOperationContext context)
        => ResumeProfileValidator.NormalizeAndValidate(raw);
}
