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
    private const int MaximumEffectiveOutputTokens = 8_192;

    public abstract string Purpose { get; }
    public abstract string PromptVersion { get; }
    public abstract string SchemaVersion { get; }
    public abstract string RubricVersion { get; }
    public abstract int MaxOutputTokens { get; }
    public abstract JsonDocument OutputSchema { get; }
    public abstract string Instructions { get; }

    /// <summary>
    /// Returns the bounded output budget for this attempt. Operations may opt into a
    /// purpose-specific second-attempt budget, but the executor remains the owner of
    /// the global two-call limit.
    /// </summary>
    public int GetEffectiveMaxOutputTokens(int attempt) => GetEffectiveMaxOutputTokens(attempt, outputTruncationRetry: false);

    public virtual int GetEffectiveMaxOutputTokens(int attempt, bool outputTruncationRetry)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);

        return ValidateEffectiveMaxOutputTokens(MaxOutputTokens);
    }

    public virtual bool SupportsOutputTruncationRetry => false;

    protected static int ValidateEffectiveMaxOutputTokens(int value) =>
        value is < 1 or > MaximumEffectiveOutputTokens
            ? throw new InvalidOperationException("AI output token budget is outside the supported bounds.")
            : value;

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

public static class StarSemantics
{
    public const string CanonicalInstructions = """
        STAR COMPONENT DEFINITIONS & EXTRACTION RULES:
        - SITUATION: Context, background, problem, or event in which the candidate acted.
          Examples: production incident, system degradation, conflict, deadline, failure, unexpected operational problem.
        - TASK: The candidate's personal responsibility, ownership, goal, or expected outcome.
          Examples: on-call responsibility, root-cause ownership, delivery target, deadline, assigned scope.
        - ACTION: Concrete steps actually performed by the candidate.
          Technical Action includes, but is NOT limited to:
          inspecting logs, querying metrics, running EXPLAIN ANALYZE, investigating pg_stat_statements, debugging, profiling, changing configuration, writing code, adding an index, implementing Redis/cache, writing migrations/scripts, deploying/rolling back, testing, mitigating an incident, making technical trade-offs, coordinating with Tech Lead, coordinating with DBA, coordinating with QA, communicating during an incident, documenting remediation.
          IMPORTANT: Technical verbs and implementation details MUST NOT be absorbed into Task just because the question asks about responsibility.
        - RESULT: Observed outcome caused by the actions.
          Examples: latency improved, error rate reduced, throughput increased, system recovered, incident resolved, release completed successfully, downtime avoided, data loss avoided, recovery happened within N minutes/hours, measurable metric improvement, RCA/runbook/documentation completed, operational/process improvement. A result does NOT have to be monetary.

        QUESTION FOCUS & SCANNING:
        - The focus of the interview question determines what should receive special attention, but it does NOT limit STAR extraction.
        - For EVERY behavioral answer: scan the ENTIRE current candidate answer for Situation, Task, Action, and Result, even when the question or follow-up focuses on only one component (e.g. if the question asks about responsibility, still detect Action and Result if present in the answer).
        - Do NOT mark Action or Result absent merely because the question primarily asked about Task.
        - Do not require artificial signpost words like 'Action:' or 'Result:'. Normal natural language technical answers must be detected directly.

        EVIDENCE-FIRST STAR EXTRACTION:
        For EACH component (situation, task, action, result):
        1. Search the candidate answer for direct evidence matching the component semantic.
        2. If direct evidence exists:
           - detected = true
           - evidence = "<concise direct quote from candidate answer>"
           - score = integer 1..100 according to specificity and quality:
             * 1-39: very weak/implicit evidence
             * 40-59: present but vague or incomplete
             * 60-79: clear and relevant evidence
             * 80-89: specific evidence with strong ownership/detail
             * 90-100: highly specific, concrete and measurable evidence
        3. If no qualifying evidence exists:
           - detected = false
           - evidence = ""
           - score = 0
        4. Write constructive feedback based on that result.
        INVARIANTS:
        - Never output detected=false with score > 0.
        - Never output detected=true with empty evidence.
        - Do not award score merely because the general rubric is high. STAR components are judged using their own evidence.
        """;
}

public static class StarComponentValidator
{
    public static AiValidationResult<StarComponentEvaluation> Validate(StarComponentEvaluation? c, string name)
    {
        if (c is null)
            return AiValidationResult<StarComponentEvaluation>.Success(new StarComponentEvaluation(0, false, string.Empty, $"Không phát hiện thành phần {name} trong câu trả lời."));

        if (c.Score is < 0 or > 100)
            return AiValidationResult<StarComponentEvaluation>.Failure("star.score_out_of_range", "semantic", repairable: true);

        var feedback = string.IsNullOrWhiteSpace(c.Feedback)
            ? (c.Detected ? $"Đã phát hiện thành phần {name} trong câu trả lời." : $"Không phát hiện thành phần {name} trong câu trả lời.")
            : c.Feedback.Trim();

        var evidence = c.Evidence?.Trim() ?? string.Empty;

        if (!c.Detected)
        {
            if (c.Score > 0)
                return AiValidationResult<StarComponentEvaluation>.Failure("star.component_detected_score_mismatch", "semantic", repairable: true);

            if (!string.IsNullOrWhiteSpace(evidence))
                return AiValidationResult<StarComponentEvaluation>.Failure("star.component_detected_score_mismatch", "semantic", repairable: true);

            return AiValidationResult<StarComponentEvaluation>.Success(new StarComponentEvaluation(0, false, string.Empty, feedback));
        }

        // Detected is true
        if (c.Score == 0)
            return AiValidationResult<StarComponentEvaluation>.Failure("star.component_detected_score_mismatch", "semantic", repairable: true);

        if (string.IsNullOrWhiteSpace(evidence))
            return AiValidationResult<StarComponentEvaluation>.Failure("star.component_detected_without_evidence", "semantic", repairable: true);

        return AiValidationResult<StarComponentEvaluation>.Success(new StarComponentEvaluation(c.Score, true, evidence, feedback));
    }
}

public static class ResumeProfileValidator
{
    public static AiValidationResult<ResumeProfile> NormalizeAndValidate(ResumeProfile? raw)
    {
        if (raw is null)
            return AiValidationResult<ResumeProfile>.Failure("resume.profile_invalid", "semantic", repairable: true);

        var experiences = raw.Experiences?
            .Select(item => new ResumeExperience(
                item.Company?.Trim(),
                item.Role?.Trim(),
                item.Start?.Trim(),
                item.End?.Trim(),
                Clean(item.Highlights)))
            .Where(HasContent)
            .ToArray() ?? [];
        var education = raw.Education?
            .Select(item => new ResumeEducation(
                item.Institution?.Trim(),
                item.Degree?.Trim(),
                item.Start?.Trim(),
                item.End?.Trim(),
                Clean(item.Details)))
            .Where(HasContent)
            .ToArray() ?? [];
        var projects = raw.Projects?
            .Select(item => new ResumeProject(
                item.Name?.Trim(),
                item.Role?.Trim(),
                Clean(item.Technologies),
                Clean(item.Highlights)))
            .Where(HasContent)
            .ToArray() ?? [];
        var normalized = new ResumeProfile(
            raw.Summary?.Trim(),
            Clean(raw.Skills),
            experiences,
            education,
            projects,
            Clean(raw.Certifications),
            Clean(raw.Languages));

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

    private static string[] Clean(IReadOnlyCollection<string>? values) =>
        values?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).ToArray() ?? [];

    private static bool HasContent(ResumeExperience item) =>
        !string.IsNullOrWhiteSpace(item.Company) || !string.IsNullOrWhiteSpace(item.Role) ||
        !string.IsNullOrWhiteSpace(item.Start) || !string.IsNullOrWhiteSpace(item.End) || item.Highlights.Count > 0;

    private static bool HasContent(ResumeEducation item) =>
        !string.IsNullOrWhiteSpace(item.Institution) || !string.IsNullOrWhiteSpace(item.Degree) ||
        !string.IsNullOrWhiteSpace(item.Start) || !string.IsNullOrWhiteSpace(item.End) || item.Details.Count > 0;

    private static bool HasContent(ResumeProject item) =>
        !string.IsNullOrWhiteSpace(item.Name) || !string.IsNullOrWhiteSpace(item.Role) ||
        item.Technologies.Count > 0 || item.Highlights.Count > 0;
}

public static class AiOperations
{
    public const string ScoreScale = "0-100";

    public static readonly ResumeProfileOperation ResumeProfile = new();
    public static readonly ResumeAnalysisOperation ResumeAnalysis = new();
    public static readonly FieldBenchmarkResumeAnalysisOperation ResumeAnalysisFieldBenchmark = new();
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
        => ResumeProfileValidator.NormalizeAndValidate(raw);
}

public static class ResumeAnalysisMetadata
{
    public const string Mode = "resume-analysis-mode";
}

public static class ResumeAnalysisValidator
{
    private const int MaximumSummaryLength = 3_000;
    private const int MaximumItemLength = 1_000;
    private const int MaximumListItems = 6;

    public static AiValidationResult<ResumeAnalysisOutput> NormalizeJobTargeted(
        ResumeAnalysisOutput? raw,
        AiOperationContext context)
    {
        if (raw is null)
            return Invalid("resume.analysis_invalid");
        if (!TryGetMode(context, out var requestedMode))
            return Invalid("resume.analysis_mode_required");
        if (!string.Equals(requestedMode, ResumeAnalysisModes.JobTargeted, StringComparison.Ordinal))
            return Invalid("resume.analysis_mode_invalid");

        if (!string.Equals(raw.Mode, ResumeAnalysisModes.JobTargeted, StringComparison.Ordinal))
            return Invalid("resume.analysis_mode_invalid");
        if (raw.MatchScore is null or < 0 or > 100)
            return Invalid("resume.match_score_invalid");
        if (raw.ReadinessScore is not null)
            return Invalid("resume.analysis_mode_mismatch");
        if (!TryNormalizeRequiredText(raw.Summary, MaximumSummaryLength, "resume.summary_blank", out var summary))
            return Invalid("resume.summary_blank");
        if (!TryNormalizeList(raw.MatchedKeywordsOrSkills, allowEmpty: true, out var matched))
            return Invalid("resume.matched_keywords_blank");
        if (!TryNormalizeList(raw.MissingKeywordsOrSkills, allowEmpty: true, out var missing))
            return Invalid("resume.missing_keywords_blank");
        if (!TryNormalizeList(raw.Strengths, allowEmpty: false, out var strengths))
            return Invalid("resume.strengths_blank");
        if (!TryNormalizeList(raw.Gaps, allowEmpty: false, out var gaps))
            return Invalid("resume.gaps_blank");
        if (!TryNormalizeList(raw.Recommendations, allowEmpty: false, out var recommendations))
            return Invalid("resume.recommendations_blank");
        if (!TryNormalizeList(raw.SectionFeedback, allowEmpty: false, out var sectionFeedback))
            return Invalid("resume.section_feedback_invalid");
        if (!TryNormalizeBreakdown(raw.Breakdown, [
                "technicalSkillMatch", "experienceRelevance", "impactEvidence", "clarity", "structure"
            ], out var breakdown))
            return Invalid("resume.breakdown_invalid");

        return AiValidationResult<ResumeAnalysisOutput>.Success(new ResumeAnalysisOutput(
            strengths,
            gaps,
            recommendations,
            raw.MatchScore,
            null,
            summary,
            matched,
            missing,
            sectionFeedback,
            breakdown,
            ResumeAnalysisModes.JobTargeted));
    }

    public static AiValidationResult<ResumeAnalysisOutput> NormalizeFieldBenchmark(
        ResumeAnalysisOutput? raw,
        AiOperationContext context)
    {
        if (raw is null)
            return Invalid("resume.analysis_invalid");
        if (!TryGetMode(context, out var requestedMode))
            return Invalid("resume.analysis_mode_required");
        if (!string.Equals(requestedMode, ResumeAnalysisModes.FieldBenchmark, StringComparison.Ordinal))
            return Invalid("resume.analysis_mode_invalid");
        if (!string.Equals(raw.Mode, ResumeAnalysisModes.FieldBenchmark, StringComparison.Ordinal))
            return Invalid("resume.analysis_mode_invalid");
        if (raw.ReadinessScore is null or < 0 or > 100)
            return Invalid("resume.readiness_score_invalid");
        if (raw.MatchScore is not null || raw.MatchedKeywordsOrSkills is not null || raw.MissingKeywordsOrSkills is not null)
            return Invalid("resume.analysis_mode_mismatch");
        if (!TryNormalizeRequiredText(raw.Summary, MaximumSummaryLength, "resume.summary_blank", out var summary))
            return Invalid("resume.summary_blank");
        if (!TryNormalizeList(raw.Strengths, allowEmpty: false, out var strengths))
            return Invalid("resume.strengths_blank");
        if (!TryNormalizeList(raw.Gaps, allowEmpty: false, out var gaps))
            return Invalid("resume.gaps_blank");
        if (!TryNormalizeList(raw.Recommendations, allowEmpty: false, out var recommendations))
            return Invalid("resume.recommendations_blank");
        if (!TryNormalizeList(raw.SectionFeedback, allowEmpty: false, out var sectionFeedback))
            return Invalid("resume.section_feedback_invalid");
        if (!TryNormalizeBreakdown(raw.Breakdown, [
                "technicalFoundation", "projectEvidence", "experiencePresentation", "impactAchievements", "clarity", "roleAlignment"
            ], out var breakdown))
            return Invalid("resume.breakdown_invalid");

        return AiValidationResult<ResumeAnalysisOutput>.Success(new ResumeAnalysisOutput(
            strengths,
            gaps,
            recommendations,
            null,
            raw.ReadinessScore,
            summary,
            null,
            null,
            sectionFeedback,
            breakdown,
            ResumeAnalysisModes.FieldBenchmark));
    }

    private static bool TryGetMode(AiOperationContext context, out string mode)
    {
        mode = string.Empty;
        if (context.Metadata is null || !context.Metadata.TryGetValue(ResumeAnalysisMetadata.Mode, out var candidate) || string.IsNullOrWhiteSpace(candidate))
            return false;

        mode = candidate;
        return true;
    }

    private static bool TryNormalizeRequiredText(string? value, int maxLength, string _, out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        return normalized.Length > 0 && normalized.Length <= maxLength;
    }

    private static bool TryNormalizeList(
        IReadOnlyCollection<string>? values,
        bool allowEmpty,
        out IReadOnlyCollection<string> normalized)
    {
        if (values is null || values.Count > MaximumListItems || (!allowEmpty && values.Count == 0) || values.Any(value => string.IsNullOrWhiteSpace(value)))
        {
            normalized = [];
            return false;
        }

        normalized = values.Select(value => value.Trim()).ToArray();
        return normalized.All(value => value.Length <= MaximumItemLength);
    }

    private static bool TryNormalizeBreakdown(
        IReadOnlyDictionary<string, int>? values,
        IReadOnlyCollection<string> requiredKeys,
        out IReadOnlyDictionary<string, int> normalized)
    {
        var normalizedValues = new Dictionary<string, int>(StringComparer.Ordinal);
        normalized = normalizedValues;
        if (values is null || values.Count != requiredKeys.Count)
            return false;

        var required = requiredKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = values
            .Select(item => (Key: item.Key.Trim(), item.Value))
            .Where(item => item.Key.Length > 0)
            .ToArray();
        if (candidates.Length != required.Count ||
            candidates.Select(item => item.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != required.Count ||
            candidates.Any(item => !required.Contains(item.Key)))
            return false;

        foreach (var key in requiredKeys)
        {
            var match = candidates.Single(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value is < 0 or > 100)
                return false;
            normalizedValues[key] = match.Value;
        }

        return normalizedValues.Count == requiredKeys.Count;
    }

    private static AiValidationResult<ResumeAnalysisOutput> Invalid(string reason) =>
        AiValidationResult<ResumeAnalysisOutput>.Failure(reason, "semantic", repairable: true);
}

public sealed class ResumeAnalysisOperation : AiOperationDefinition<ResumeAnalysisOutput>
{
    private const int InitialOutputTokens = 4_096;
    private const int TruncationRetryOutputTokens = 8_192;

    public override string Purpose => AiPurposes.ResumeAnalysis;
    public override string PromptVersion => "resume-analysis-job-targeted-v2";
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
        "Analyze the candidate's resume profile against the target job description. Return mode='job_targeted', a 0-100 matchScore, a concise grounded summary, matched and missing skills (which may be empty when none are evidenced), 1-6 strengths, gaps and recommendations, sectionFeedback, and the five-key 0-100 breakdown. Never invent candidate achievements or qualifications. Write in the language of the supplied job description.";

    public override AiValidationResult<ResumeAnalysisOutput> NormalizeAndValidate(ResumeAnalysisOutput? raw, AiOperationContext context) =>
        ResumeAnalysisValidator.NormalizeJobTargeted(raw, context);
}

public sealed class FieldBenchmarkResumeAnalysisOperation : AiOperationDefinition<ResumeAnalysisOutput>
{
    private const int InitialOutputTokens = 4_096;
    private const int TruncationRetryOutputTokens = 8_192;

    public override string Purpose => AiPurposes.ResumeAnalysis;
    public override string PromptVersion => "resume-analysis-field-benchmark-v2";
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
        "Benchmark the candidate's resume profile for the supplied industry, target role and seniority. Return mode='field_benchmark', a 0-100 readinessScore, concise grounded summary, 1-6 strengths, gaps and recommendations, sectionFeedback, and the six-key 0-100 breakdown. Never invent candidate achievements or qualifications. Write in the language of the supplied context.";

    public override AiValidationResult<ResumeAnalysisOutput> NormalizeAndValidate(ResumeAnalysisOutput? raw, AiOperationContext context) =>
        ResumeAnalysisValidator.NormalizeFieldBenchmark(raw, context);
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
    public override string PromptVersion => "interview-eval-v4";
    public override string SchemaVersion => "interview-eval-v4";
    public override string RubricVersion => "rubric-v2";
    public override int MaxOutputTokens => 6_000;

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
                "overallScore": { "type": "integer", "minimum": 0, "maximum": 100, "nullable": true },
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
              "required": ["applicable"]
            }
          },
          "required": ["scoreScale", "scores", "feedback"]
        }
        """);

    public override string Instructions =>
        $"""
        Evaluate the candidate's answer against the job and question requirements.
        Set scoreScale to '0-100'.
        Return exactly four rubric scores for criteria: correctness, structure, completeness, clarity (scores 0-100 with non-empty evidence quote).
        Write in the same language as the interview.

        If the question is technical or non-behavioral:
        Set star.applicable = false, omit component details.

        If the question is behavioral:
        Set star.applicable = true.
        {StarSemantics.CanonicalInstructions}
        """;

    public override AiValidationResult<AnswerEvaluation> NormalizeAndValidate(AnswerEvaluation? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<AnswerEvaluation>.Failure("rubric.criteria_missing", "semantic", repairable: true);
        if (!string.Equals(raw.ScoreScale, AiOperations.ScoreScale, StringComparison.Ordinal))
            return AiValidationResult<AnswerEvaluation>.Failure("score.scale_invalid", "semantic", repairable: true);

        var rubricResult = CanonicalRubricValidator.ValidateAndNormalize(raw.Scores);
        if (!rubricResult.IsValid)
            return AiValidationResult<AnswerEvaluation>.Failure(rubricResult.FailureReason!, rubricResult.ValidationStage!, rubricResult.Repairable);

        if (string.IsNullOrWhiteSpace(raw.Feedback))
            return AiValidationResult<AnswerEvaluation>.Failure("interview.feedback_blank", "semantic", repairable: true);
        var feedback = raw.Feedback.Trim();

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
                raw.Star?.CoachingTips?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [],
                AiOperations.ScoreScale);
        }
        else
        {
            // Server expects STAR=true
            if (raw.Star is null)
                return AiValidationResult<AnswerEvaluation>.Failure("star.missing", "semantic", repairable: true);

            if (!raw.Star.Applicable)
                return AiValidationResult<AnswerEvaluation>.Failure("star.applicability_mismatch", "semantic", repairable: true);

            var sitResult = StarComponentValidator.Validate(raw.Star.Situation, "situation");
            if (!sitResult.IsValid)
                return AiValidationResult<AnswerEvaluation>.Failure(sitResult.FailureReason!, sitResult.ValidationStage!, sitResult.Repairable);

            var taskResult = StarComponentValidator.Validate(raw.Star.Task, "task");
            if (!taskResult.IsValid)
                return AiValidationResult<AnswerEvaluation>.Failure(taskResult.FailureReason!, taskResult.ValidationStage!, taskResult.Repairable);

            var actResult = StarComponentValidator.Validate(raw.Star.Action, "action");
            if (!actResult.IsValid)
                return AiValidationResult<AnswerEvaluation>.Failure(actResult.FailureReason!, actResult.ValidationStage!, actResult.Repairable);

            var resResult = StarComponentValidator.Validate(raw.Star.Result, "result");
            if (!resResult.IsValid)
                return AiValidationResult<AnswerEvaluation>.Failure(resResult.FailureReason!, resResult.ValidationStage!, resResult.Repairable);

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

            normalizedStar = new StarEvaluation(
                true,
                overallScore,
                sit,
                task,
                act,
                res,
                missing,
                raw.Star.Strengths?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [],
                raw.Star.CoachingTips?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).Take(3).ToArray() ?? [],
                AiOperations.ScoreScale);
        }

        return AiValidationResult<AnswerEvaluation>.Success(
            new AnswerEvaluation(rubricResult.NormalizedValue!, feedback, normalizedStar, AiOperations.ScoreScale));
    }

    public override string BuildRepairInstructions(AiValidationResult<AnswerEvaluation> priorResult, string originalInstructions)
    {
        if (priorResult.FailureReason?.StartsWith("star.", StringComparison.Ordinal) == true)
        {
            return $"""
                {originalInstructions}

                IMPORTANT STAR CORRECTION INSTRUCTION:
                The previous structured evaluation violated the STAR contract: '{priorResult.FailureReason}'.
                Re-evaluate the ORIGINAL candidate answer.
                Important:
                - The question focus does not restrict STAR extraction; inspect the entire candidate answer.
                - Extract evidence before assigning detected/score.
                - Concrete technical actions (e.g. profiling, queries, coding, caching, indexing, coordinating) are Action.
                - Measurable operational outcomes (e.g. latency, recovery, runbook, metrics) are Result.
                - detected=false requires score=0 and empty evidence.
                - detected=true requires score 1-100 and direct evidence quote.
                Return a completely corrected object matching the schema.
                """;
        }

        return base.BuildRepairInstructions(priorResult, originalInstructions);
    }
}

public sealed class InterviewReportOperation : AiOperationDefinition<InterviewReportOutput>
{
    public override string Purpose => AiPurposes.InterviewReport;
    public override string PromptVersion => "interview-report-v2";
    public override string SchemaVersion => "interview-report-v2";
    public override string RubricVersion => "rubric-v2";
    public override int MaxOutputTokens => 6_000;

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
          "required": ["scoreScale", "scores", "strengths", "gaps", "actionPlan"]
        }
        """);

    public override string Instructions =>
        "Synthesize the interview transcript into an authoritative final coaching report. Set scoreScale to '0-100'. Return exactly four scores with criterion values correctness, structure, completeness, and clarity (integer scores 0-100 with evidence citing the transcript). Return 1 to 3 grounded strengths, 1 to 3 clear gaps, and 1 to 3 concrete actionPlan items. Do not leave any array empty. Write in the same language as the interview.";

    public override AiValidationResult<InterviewReportOutput> NormalizeAndValidate(InterviewReportOutput? raw, AiOperationContext context)
    {
        if (raw is null)
            return AiValidationResult<InterviewReportOutput>.Failure("rubric.criteria_missing", "semantic", repairable: true);
        if (!string.Equals(raw.ScoreScale, AiOperations.ScoreScale, StringComparison.Ordinal))
            return AiValidationResult<InterviewReportOutput>.Failure("score.scale_invalid", "semantic", repairable: true);

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
            new InterviewReportOutput(rubricResult.NormalizedValue!, strengths, gaps, actionPlan, AiOperations.ScoreScale));
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
          "required": ["scoreScale", "overallScore", "dimensions", "strengths", "gaps", "recommendedApproach", "feedback"]
        }
        """);

    public override string Instructions =>
        "Evaluate the candidate's scenario response against the scenario requirements, difficulty, and target competency. Set scoreScale to '0-100'. Return overallScore (integer 0-100), dimensions array (2 to 4 dimensions; each with criterion, score 0-100, non-empty evidence quote from answer, and actionable feedback), strengths (1-3 items), gaps (1-3 items), recommendedApproach (1-3 actionable steps), and overall feedback summary. Write in the same language as the answer.";

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

public sealed class StarEvaluateOperation : AiOperationDefinition<StarEvaluation>
{
    public override string Purpose => AiPurposes.StarEvaluate;
    public override string PromptVersion => "star-eval-v3";
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
        Write in the same language as the answer.
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
