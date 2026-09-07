using System.Text.Json;
using Nexora.Business.Practice;

namespace Nexora.Business.Ai;

public static class AiPurposes
{
    public const string ResumeProfile = "resume.profile";
    public const string ResumeAnalysis = "resume.analysis";
    public const string InterviewFirstQuestion = "interview.first-question";
    public const string InterviewEvaluate = "interview.evaluate";
    public const string InterviewFollowup = "interview.followup";
    public const string InterviewReport = "interview.report";
    public const string ScenarioEvaluate = "scenario.evaluate";
    public const string StarEvaluate = "star.evaluate";
}

public abstract class AiOperationDefinition<T>
{
    public abstract string Purpose { get; }
    public abstract string PromptVersion { get; }
    public abstract string SchemaVersion { get; }
    public abstract string RubricVersion { get; }
    public abstract int MaxOutputTokens { get; }
    public abstract JsonDocument OutputSchema { get; }
    public abstract string Instructions { get; }

    public abstract AiValidationResult<T> NormalizeAndValidate(T? raw, AiOperationContext context);

    public virtual string BuildRepairInstructions(AiValidationResult<T> priorResult, string originalInstructions)
    {
        return $"""
            {originalInstructions}

            IMPORTANT CORRECTION INSTRUCTION:
            Your previous output failed Nexora validation due to reason: '{priorResult.FailureReason}'.
            You must fix this error immediately in your response.
            Strictly comply with all required fields, criteria enums, 0-100 integer score ranges, and non-empty evidence.
            """;
    }
}

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

public static class AiOperations
{
    public static readonly ResumeProfileOperation ResumeProfile = new();
    public static readonly ResumeAnalysisOperation ResumeAnalysis = new();
    public static readonly InterviewFirstQuestionOperation InterviewFirstQuestion = new();
    public static readonly InterviewEvaluateOperation InterviewEvaluate = new();
    public static readonly InterviewFollowupOperation InterviewFollowup = new();
    public static readonly InterviewReportOperation InterviewReport = new();
    public static readonly ScenarioEvaluateOperation ScenarioEvaluate = new();
    public static readonly StarEvaluateOperation StarEvaluate = new();
}

public sealed class ResumeProfileOperation : AiOperationDefinition<ResumeProfile>
{
    public override string Purpose => AiPurposes.ResumeProfile;
    public override string PromptVersion => "resume-profile-v2";
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
        "Extract a faithful, structured resume profile strictly from the provided resume text. Never infer or fabricate details. For any missing section, return an empty array. Do not fail if optional sections are absent. Write in the language of the resume.";

    public override AiValidationResult<ResumeProfile> NormalizeAndValidate(ResumeProfile? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<ResumeProfile>.Failure("resume.profile_invalid", "semantic", repairable: true);

        var normalized = new ResumeProfile(
            raw.Summary?.Trim(),
            raw.Skills?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray() ?? [],
            raw.Experiences ?? [],
            raw.Education ?? [],
            raw.Projects ?? [],
            raw.Certifications?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray() ?? [],
            raw.Languages?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray() ?? []);

        var hasContent = !string.IsNullOrWhiteSpace(normalized.Summary) ||
            normalized.Skills.Count > 0 ||
            normalized.Experiences.Count > 0 ||
            normalized.Education.Count > 0 ||
            normalized.Projects.Count > 0 ||
            normalized.Certifications.Count > 0 ||
            normalized.Languages.Count > 0;

        if (!hasContent)
            return AiValidationResult<ResumeProfile>.Failure("resume.profile_invalid", "semantic", repairable: true);

        if ((normalized.Summary?.Length ?? 0) > 3_000 ||
            normalized.Skills.Count > 100 ||
            normalized.Experiences.Count > 30 ||
            normalized.Education.Count > 20 ||
            normalized.Projects.Count > 30 ||
            normalized.Certifications.Count > 50 ||
            normalized.Languages.Count > 30)
            return AiValidationResult<ResumeProfile>.Failure("resume.profile_invalid", "semantic", repairable: false);

        return AiValidationResult<ResumeProfile>.Success(normalized);
    }
}

public sealed class ResumeAnalysisOperation : AiOperationDefinition<ResumeAnalysisOutput>
{
    public override string Purpose => AiPurposes.ResumeAnalysis;
    public override string PromptVersion => "resume-analysis-v2";
    public override string SchemaVersion => "resume-analysis-v2";
    public override string RubricVersion => "analysis-v2";
    public override int MaxOutputTokens => 1_500;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "strengths": { "type": "array", "items": { "type": "string" } },
            "gaps": { "type": "array", "items": { "type": "string" } },
            "recommendations": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["strengths", "gaps", "recommendations"]
        }
        """);

    public override string Instructions =>
        "Analyze the candidate's profile against the target job description. Return strengths (1-3 grounded items directly matching job requirements), gaps (1-3 specific missing qualifications or skills), and recommendations (1-3 actionable steps to increase readiness). Do not leave any array empty. Write in the language of the job description.";

    public override AiValidationResult<ResumeAnalysisOutput> NormalizeAndValidate(ResumeAnalysisOutput? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<ResumeAnalysisOutput>.Failure("resume.analysis_invalid", "semantic", repairable: true);

        var strengths = raw.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var gaps = raw.Gaps?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var recommendations = raw.Recommendations?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];

        if (strengths.Length == 0 || gaps.Length == 0 || recommendations.Length == 0)
            return AiValidationResult<ResumeAnalysisOutput>.Failure("resume.analysis_invalid", "semantic", repairable: true);

        return AiValidationResult<ResumeAnalysisOutput>.Success(new ResumeAnalysisOutput(strengths, gaps, recommendations));
    }
}

public sealed class InterviewFirstQuestionOperation : AiOperationDefinition<GeneratedQuestion>
{
    public override string Purpose => AiPurposes.InterviewFirstQuestion;
    public override string PromptVersion => "interview-first-question-v2";
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
        "Generate one concise, realistic interview question appropriate for the supplied role, seniority, and interview type. If interview type is behavioral, craft a behavioral question inviting a real-world story. The question must be under 2,000 characters. Do not mention or explain the STAR acronym. Write in the language of the role and job description.";

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

public sealed class InterviewFollowupOperation : AiOperationDefinition<GeneratedQuestion>
{
    public override string Purpose => AiPurposes.InterviewFollowup;
    public override string PromptVersion => "interview-followup-v2";
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
        "Generate one concise, natural follow-up interview question based on the candidate's previous answer and context. If STAR missing elements or coaching tips are provided, probe for the missing details (such as specific actions taken, technical decisions, or measurable impact) without mechanically using the word 'STAR'. The question must be under 2,000 characters. Write in the same language as the interview.";

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

public sealed class InterviewEvaluateOperation : AiOperationDefinition<AnswerEvaluation>
{
    public override string Purpose => AiPurposes.InterviewEvaluate;
    public override string PromptVersion => "interview-eval-v3";
    public override string SchemaVersion => "interview-eval-v3";
    public override string RubricVersion => "rubric-v2";
    public override int MaxOutputTokens => 2_000;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "scoreScale": { "type": "string", "enum": ["0-100"] },
            "scores": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "criterion": { "type": "string", "enum": ["correctness", "structure", "completeness", "clarity"] },
                  "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                  "evidence": { "type": "string" }
                },
                "required": ["criterion", "score", "evidence"]
              }
            },
            "feedback": { "type": "string" },
            "star": {
              "type": "object",
              "properties": {
                "applicable": { "type": "boolean" },
                "overallScore": { "type": "integer", "nullable": true },
                "situation": {
                  "type": "object",
                  "properties": {
                    "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                    "detected": { "type": "boolean" },
                    "evidence": { "type": "string" },
                    "feedback": { "type": "string" }
                  },
                  "required": ["score", "detected", "evidence", "feedback"],
                  "nullable": true
                },
                "task": {
                  "type": "object",
                  "properties": {
                    "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                    "detected": { "type": "boolean" },
                    "evidence": { "type": "string" },
                    "feedback": { "type": "string" }
                  },
                  "required": ["score", "detected", "evidence", "feedback"],
                  "nullable": true
                },
                "action": {
                  "type": "object",
                  "properties": {
                    "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                    "detected": { "type": "boolean" },
                    "evidence": { "type": "string" },
                    "feedback": { "type": "string" }
                  },
                  "required": ["score", "detected", "evidence", "feedback"],
                  "nullable": true
                },
                "result": {
                  "type": "object",
                  "properties": {
                    "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                    "detected": { "type": "boolean" },
                    "evidence": { "type": "string" },
                    "feedback": { "type": "string" }
                  },
                  "required": ["score", "detected", "evidence", "feedback"],
                  "nullable": true
                },
                "missingElements": { "type": "array", "items": { "type": "string" } },
                "strengths": { "type": "array", "items": { "type": "string" } },
                "coachingTips": { "type": "array", "items": { "type": "string" } }
              },
              "required": ["applicable"]
            }
          },
          "required": ["scores", "feedback"]
        }
        """);

    public override string Instructions =>
        "Evaluate the candidate's answer against the job and question requirements. Set scoreScale to '0-100'. Return exactly four rubric scores for criteria: correctness, structure, completeness, clarity (scores 0-100 with non-empty evidence quote). If the question is behavioral, set star.applicable=true and provide situation, task, action, and result details with scores 0-100. If the question is technical or non-behavioral, set star.applicable=false and omit component details. Write in the same language as the interview.";

    public override AiValidationResult<AnswerEvaluation> NormalizeAndValidate(AnswerEvaluation? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<AnswerEvaluation>.Failure("rubric.criteria_missing", "semantic", repairable: true);

        var rubricResult = CanonicalRubricValidator.ValidateAndNormalize(raw.Scores);
        if (!rubricResult.IsValid)
            return AiValidationResult<AnswerEvaluation>.Failure(rubricResult.FailureReason!, rubricResult.ValidationStage!, rubricResult.Repairable);

        var feedback = raw.Feedback?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(feedback))
            return AiValidationResult<AnswerEvaluation>.Failure("rubric.feedback_blank", "semantic", repairable: true);

        // Server authoritative STAR validation
        var expectedStar = context.ExpectedStar ?? false;
        StarEvaluation normalizedStar;

        if (!expectedStar)
        {
            // If server expects non-behavioral, normalize to false regardless of model opinion
            normalizedStar = new StarEvaluation(
                false,
                null,
                null,
                null,
                null,
                null,
                [],
                [],
                raw.Star?.CoachingTips?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? []);
        }
        else
        {
            // Server expects STAR=true
            if (raw.Star is null)
                return AiValidationResult<AnswerEvaluation>.Failure("star.missing", "semantic", repairable: true);

            if (!raw.Star.Applicable)
                return AiValidationResult<AnswerEvaluation>.Failure("star.applicability_mismatch", "semantic", repairable: true);

            var sit = ValidateStarComponent(raw.Star.Situation, "situation");
            var task = ValidateStarComponent(raw.Star.Task, "task");
            var act = ValidateStarComponent(raw.Star.Action, "action");
            var res = ValidateStarComponent(raw.Star.Result, "result");

            if (sit is null || task is null || act is null || res is null)
                return AiValidationResult<AnswerEvaluation>.Failure("star.component_invalid", "semantic", repairable: true);

            if (sit.Score is < 0 or > 100 || task.Score is < 0 or > 100 || act.Score is < 0 or > 100 || res.Score is < 0 or > 100)
                return AiValidationResult<AnswerEvaluation>.Failure("star.score_out_of_range", "semantic", repairable: true);

            var overallScore = (int)Math.Round(sit.Score * 0.20 + task.Score * 0.20 + act.Score * 0.35 + res.Score * 0.25);
            var missing = new[] { ("situation", sit), ("task", task), ("action", act), ("result", res) }
                .Where(x => !x.Item2.Detected || x.Item2.Score < 60)
                .Select(x => x.Item1)
                .Concat(raw.Star.MissingElements ?? [])
                .Where(x => x is "situation" or "task" or "action" or "result")
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            normalizedStar = new StarEvaluation(
                true,
                overallScore,
                sit,
                task,
                act,
                res,
                missing,
                raw.Star.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [],
                raw.Star.CoachingTips?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? []);
        }

        return AiValidationResult<AnswerEvaluation>.Success(new AnswerEvaluation(rubricResult.NormalizedValue!, feedback, normalizedStar));
    }

    private static StarComponentEvaluation? ValidateStarComponent(StarComponentEvaluation? c, string name)
    {
        if (c is null) return null;
        var score = Math.Clamp(c.Score, 0, 100);
        var feedback = string.IsNullOrWhiteSpace(c.Feedback) ? $"Đánh giá {name}." : c.Feedback.Trim();
        var evidence = c.Evidence?.Trim() ?? string.Empty;
        var detected = c.Detected || (!string.IsNullOrWhiteSpace(evidence) && score >= 50);
        return new StarComponentEvaluation(score, detected, evidence, feedback);
    }
}

public sealed class InterviewReportOperation : AiOperationDefinition<InterviewReportOutput>
{
    public override string Purpose => AiPurposes.InterviewReport;
    public override string PromptVersion => "interview-report-v2";
    public override string SchemaVersion => "interview-report-v2";
    public override string RubricVersion => "rubric-v2";
    public override int MaxOutputTokens => 2_000;

    public override JsonDocument OutputSchema { get; } = JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "scoreScale": { "type": "string", "enum": ["0-100"] },
            "scores": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "criterion": { "type": "string", "enum": ["correctness", "structure", "completeness", "clarity"] },
                  "score": { "type": "integer", "minimum": 0, "maximum": 100 },
                  "evidence": { "type": "string" }
                },
                "required": ["criterion", "score", "evidence"]
              }
            },
            "strengths": { "type": "array", "items": { "type": "string" } },
            "gaps": { "type": "array", "items": { "type": "string" } },
            "actionPlan": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["scores", "strengths", "gaps", "actionPlan"]
        }
        """);

    public override string Instructions =>
        "Synthesize the interview transcript into an authoritative final coaching report. Set scoreScale to '0-100'. Return exactly four scores with criterion values correctness, structure, completeness, and clarity (integer scores 0-100 with evidence citing the transcript). Return 1 to 3 grounded strengths, 1 to 3 clear gaps, and 1 to 3 concrete actionPlan items. Do not leave any array empty. Write in the same language as the interview.";

    public override AiValidationResult<InterviewReportOutput> NormalizeAndValidate(InterviewReportOutput? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<InterviewReportOutput>.Failure("rubric.criteria_missing", "semantic", repairable: true);

        var rubricResult = CanonicalRubricValidator.ValidateAndNormalize(raw.Scores);
        if (!rubricResult.IsValid)
            return AiValidationResult<InterviewReportOutput>.Failure(rubricResult.FailureReason!, rubricResult.ValidationStage!, rubricResult.Repairable);

        var strengths = raw.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var gaps = raw.Gaps?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var actionPlan = raw.ActionPlan?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];

        if (strengths.Length == 0)
            return AiValidationResult<InterviewReportOutput>.Failure("report.strengths_blank", "semantic", repairable: true);
        if (gaps.Length == 0)
            return AiValidationResult<InterviewReportOutput>.Failure("report.gaps_blank", "semantic", repairable: true);
        if (actionPlan.Length == 0)
            return AiValidationResult<InterviewReportOutput>.Failure("report.action_plan_blank", "semantic", repairable: true);

        return AiValidationResult<InterviewReportOutput>.Success(
            new InterviewReportOutput(rubricResult.NormalizedValue!, strengths, gaps, actionPlan));
    }
}

public sealed class ScenarioEvaluateOperation : AiOperationDefinition<ScenarioEvaluationResult>
{
    public override string Purpose => AiPurposes.ScenarioEvaluate;
    public override string PromptVersion => "scenario-eval-v2";
    public override string SchemaVersion => "scenario-eval-v2";
    public override string RubricVersion => "scenario-rubric-v2";
    public override int MaxOutputTokens => 2_000;

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
          "required": ["overallScore", "dimensions", "strengths", "gaps", "recommendedApproach", "feedback"]
        }
        """);

    public override string Instructions =>
        "Evaluate the candidate's scenario response against the scenario requirements, difficulty, and target competency. Set scoreScale to '0-100'. Return overallScore (integer 0-100), dimensions array (2 to 4 dimensions; each with criterion, score 0-100, non-empty evidence quote from answer, and actionable feedback), strengths (1-3 items), gaps (1-3 items), recommendedApproach (1-3 actionable steps), and overall feedback summary. Write in the same language as the answer.";

    public override AiValidationResult<ScenarioEvaluationResult> NormalizeAndValidate(ScenarioEvaluationResult? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.dimensions_missing", "semantic", repairable: true);

        if (raw.OverallScore is < 0 or > 100)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.overall_score_out_of_range", "semantic", repairable: true);

        if (raw.Dimensions is null || raw.Dimensions.Count < 2 || raw.Dimensions.Count > 6)
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.dimensions_missing", "semantic", repairable: true);

        var normalizedDimensions = new List<ScenarioDimensionEvaluation>(raw.Dimensions.Count);
        foreach (var d in raw.Dimensions)
        {
            if (string.IsNullOrWhiteSpace(d.Criterion) || d.Score is < 0 or > 100 ||
                string.IsNullOrWhiteSpace(d.Evidence) || string.IsNullOrWhiteSpace(d.Feedback))
            {
                return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.dimension_invalid", "semantic", repairable: true);
            }
            normalizedDimensions.Add(new ScenarioDimensionEvaluation(d.Criterion.Trim(), d.Score, d.Evidence.Trim(), d.Feedback.Trim()));
        }

        var strengths = raw.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var gaps = raw.Gaps?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var approach = raw.RecommendedApproach?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var feedback = raw.Feedback?.Trim() ?? string.Empty;

        if (strengths.Length == 0 || gaps.Length == 0 || approach.Length == 0 || string.IsNullOrWhiteSpace(feedback))
            return AiValidationResult<ScenarioEvaluationResult>.Failure("scenario.feedback_invalid", "semantic", repairable: true);

        return AiValidationResult<ScenarioEvaluationResult>.Success(
            new ScenarioEvaluationResult(raw.OverallScore, normalizedDimensions, strengths, gaps, approach, feedback));
    }
}

public sealed class StarEvaluateOperation : AiOperationDefinition<StarEvaluation>
{
    public override string Purpose => AiPurposes.StarEvaluate;
    public override string PromptVersion => "star-eval-v2";
    public override string SchemaVersion => "star-eval-v2";
    public override string RubricVersion => "star-rubric-v2";
    public override int MaxOutputTokens => 2_000;

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
          "required": ["applicable", "overallScore", "situation", "task", "action", "result", "missingElements", "strengths", "coachingTips"]
        }
        """);

    public override string Instructions =>
        "Evaluate the candidate's answer using the STAR methodology (Situation, Task, Action, Result). Set scoreScale to '0-100' and applicable to true. Thoroughly assess all four components with integer scores strictly between 0 and 100. Do NOT use 1-5 scale. overallScore must be an integer 0-100 reflecting the overall quality. missingElements must list component names that are absent or scored below 60. strengths: 1-3 specific strong points. coachingTips: 1-3 actionable improvement tips. Write in the same language as the answer.";

    public override AiValidationResult<StarEvaluation> NormalizeAndValidate(StarEvaluation? raw, AiOperationContext context)
    {
        if (raw is null || !raw.Applicable)
            return AiValidationResult<StarEvaluation>.Failure("star.applicability_mismatch", "semantic", repairable: true);

        var sit = ValidateComponent(raw.Situation, "situation");
        var task = ValidateComponent(raw.Task, "task");
        var act = ValidateComponent(raw.Action, "action");
        var res = ValidateComponent(raw.Result, "result");

        if (sit is null || task is null || act is null || res is null)
            return AiValidationResult<StarEvaluation>.Failure("star.component_invalid", "semantic", repairable: true);

        if (sit.Score is < 0 or > 100 || task.Score is < 0 or > 100 || act.Score is < 0 or > 100 || res.Score is < 0 or > 100)
            return AiValidationResult<StarEvaluation>.Failure("star.score_out_of_range", "semantic", repairable: true);

        var overallScore = (int)Math.Round(sit.Score * 0.20 + task.Score * 0.20 + act.Score * 0.35 + res.Score * 0.25);
        var missing = new[] { ("situation", sit), ("task", task), ("action", act), ("result", res) }
            .Where(x => !x.Item2.Detected || x.Item2.Score < 60)
            .Select(x => x.Item1)
            .Concat(raw.MissingElements ?? [])
            .Where(x => x is "situation" or "task" or "action" or "result")
            .Distinct(StringComparer.Ordinal)
            .Take(4)
            .ToArray();

        var strengths = raw.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];
        var coachingTips = raw.CoachingTips?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [];

        return AiValidationResult<StarEvaluation>.Success(new StarEvaluation(
            true,
            overallScore,
            sit,
            task,
            act,
            res,
            missing,
            strengths,
            coachingTips));
    }

    private static StarComponentEvaluation? ValidateComponent(StarComponentEvaluation? c, string name)
    {
        if (c is null) return null;
        var score = Math.Clamp(c.Score, 0, 100);
        var feedback = string.IsNullOrWhiteSpace(c.Feedback) ? $"Đánh giá {name}." : c.Feedback.Trim();
        var evidence = c.Evidence?.Trim() ?? string.Empty;
        var detected = c.Detected || (!string.IsNullOrWhiteSpace(evidence) && score >= 50);
        return new StarComponentEvaluation(score, detected, evidence, feedback);
    }
}
