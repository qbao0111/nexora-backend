using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Practice;
using Nexora.Business.Skills;
using Nexora.Data.Persistence;

namespace Nexora.Data.Skills;

public sealed class SkillProfileService(NexoraDbContext dbContext) : ISkillProfileService
{
    private const int MaximumQualitativeLabelLength = 1_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ScenarioEvaluateOperation ScenarioOperation = new();

    public async Task<SkillProfileView> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var resumeAnalyses = await dbContext.ResumeAnalyses.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == PracticeValues.Completed && item.Result != null)
            .Select(item => new ResumeAnalysisRow(item.Id, item.Mode, item.Result, item.CompletedAt, item.UpdatedAt))
            .ToArrayAsync(cancellationToken);

        var reports = await dbContext.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId && item.InterviewSession.UserId == userId)
            .Select(item => new InterviewReportRow(item.Id, item.InterviewSessionId, item.Rubric, item.CreatedAt))
            .ToArrayAsync(cancellationToken);

        var answers = await dbContext.InterviewAnswers.AsNoTracking()
            .Where(item => item.UserId == userId && item.InterviewSession.UserId == userId)
            .Select(item => new InterviewAnswerRow(item.Id, item.InterviewSessionId, item.Evaluation, item.CreatedAt))
            .ToArrayAsync(cancellationToken);

        var starAttempts = await dbContext.StarAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed && item.EvaluationJson != null)
            .Select(item => new StarAttemptRow(item.Id, item.EvaluationJson, item.CompletedAt, item.UpdatedAt))
            .ToArrayAsync(cancellationToken);

        var scenarioAttempts = await dbContext.ScenarioAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed &&
                           item.EvaluationJson != null && item.Scenario.Competency != null && item.Scenario.Competency != string.Empty)
            .Select(item => new ScenarioAttemptRow(item.Id, item.Scenario.Competency, item.EvaluationJson, item.CompletedAt, item.UpdatedAt))
            .ToArrayAsync(cancellationToken);

        var evidence = new List<SkillProfileEvidence>();
        ResumeAnalysisSelection? latestValidResumeAnalysis = null;
        foreach (var row in resumeAnalyses
                     .OrderByDescending(item => item.CompletedAt ?? item.UpdatedAt)
                     .ThenByDescending(item => item.Id))
        {
            if (!TryReadValidatedResumeAnalysis(row, out var output)) continue;

            ReadResumeAnalysisEvidence(row, output, evidence);
            latestValidResumeAnalysis ??= new ResumeAnalysisSelection(row, output);
        }

        var weaknessSignals = latestValidResumeAnalysis is null
            ? new List<SkillProfileWeaknessSignal>()
            : ReadResumeAnalysisWeaknessSignals(latestValidResumeAnalysis.Row, latestValidResumeAnalysis.Output);

        var validReportSessions = new HashSet<Guid>();
        foreach (var row in reports)
        {
            if (!TryReadCanonicalRubric(row.Rubric, out var scores)) continue;

            validReportSessions.Add(row.InterviewSessionId);
            foreach (var score in scores)
                AddRubricEvidence(evidence, $"interview-report:{row.Id:N}:{score.Criterion}", score, row.CreatedAt);
        }

        foreach (var row in answers)
        {
            if (!TryReadAnswerEvaluation(row.Evaluation, out var evaluation)) continue;

            if (!validReportSessions.Contains(row.InterviewSessionId) &&
                TryReadCanonicalRubric(evaluation.Scores, out var scores))
            {
                foreach (var score in scores)
                    AddRubricEvidence(evidence, $"interview-answer:{row.Id:N}:{score.Criterion}", score, row.CreatedAt);
            }

            AddStarEvidence(evidence, evaluation.Star, SkillProfileSourceTypes.Interview,
                $"interview-answer:{row.Id:N}:star", row.CreatedAt);
        }

        foreach (var row in starAttempts)
        {
            if (!TryDeserialize(row.EvaluationJson, out StarEvaluation? evaluation)) continue;
            AddStarEvidence(evidence, evaluation, SkillProfileSourceTypes.StarAttempt,
                $"star-attempt:{row.Id:N}", row.CompletedAt ?? row.UpdatedAt);
        }

        foreach (var row in scenarioAttempts)
            ReadScenarioAttempt(row, evidence);

        return SkillProfileAggregator.Aggregate(evidence, weaknessSignals);
    }

    private static void ReadResumeAnalysisEvidence(
        ResumeAnalysisRow row,
        ResumeAnalysisOutput output,
        List<SkillProfileEvidence> evidence)
    {
        var timestamp = row.CompletedAt ?? row.UpdatedAt;
        foreach (var item in output.Breakdown!.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var code = SkillProfileTaxonomy.CreateCode("resume", item.Key);
            if (code is null) continue;

            evidence.Add(new SkillProfileEvidence(
                $"resume-analysis:{row.Id:N}:{code}",
                code,
                SkillProfileTaxonomy.DisplayName(item.Key),
                "resume",
                SkillProfileSourceTypes.ResumeAnalysis,
                item.Value,
                timestamp));
        }
    }

    private static List<SkillProfileWeaknessSignal> ReadResumeAnalysisWeaknessSignals(
        ResumeAnalysisRow row,
        ResumeAnalysisOutput output)
    {
        var timestamp = row.CompletedAt ?? row.UpdatedAt;
        return (output.Gaps ?? []).Concat(output.MissingKeywordsOrSkills ?? [])
            .Take(12)
            .Select(label => label?.Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label) && label.Length <= MaximumQualitativeLabelLength)
            .Select(label => new SkillProfileWeaknessSignal(SkillProfileSourceTypes.ResumeAnalysis, label!, timestamp))
            .ToList();
    }

    private static bool TryReadValidatedResumeAnalysis(
        ResumeAnalysisRow row,
        out ResumeAnalysisOutput output)
    {
        output = null!;
        if (!TryDeserialize(row.Result, out ResumeAnalysisOutput? parsed) || parsed is null) return false;
        if (!ResumeAnalysisModes.TryParse(row.Mode, out var mode)) return false;

        var context = new AiOperationContext(
            row.Id.ToString("N"),
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ResumeAnalysisMetadata.Mode] = mode.ToWireValue()
            });
        var validation = mode == ResumeAnalysisMode.JobTargeted
            ? ResumeAnalysisValidator.NormalizeJobTargeted(parsed, context)
            : ResumeAnalysisValidator.NormalizeFieldBenchmark(parsed, context);
        if (!validation.IsValid || validation.NormalizedValue?.Breakdown is null) return false;

        output = validation.NormalizedValue;
        return true;
    }

    private static void ReadScenarioAttempt(ScenarioAttemptRow row, List<SkillProfileEvidence> evidence)
    {
        var code = SkillProfileTaxonomy.CreateCode("scenario", row.Competency);
        if (code is null || !TryDeserialize(row.EvaluationJson, out ScenarioEvaluationResult? raw) || raw is null) return;

        var validation = ScenarioOperation.NormalizeAndValidate(raw, new AiOperationContext(row.Id.ToString("N")));
        if (!validation.IsValid || validation.NormalizedValue is null) return;

        evidence.Add(new SkillProfileEvidence(
            $"scenario-attempt:{row.Id:N}",
            code,
            SkillProfileTaxonomy.DisplayName(row.Competency),
            "scenario",
            SkillProfileSourceTypes.Scenario,
            validation.NormalizedValue.OverallScore,
            row.CompletedAt ?? row.UpdatedAt));
    }

    private static void AddRubricEvidence(
        List<SkillProfileEvidence> evidence,
        string identity,
        RubricScore score,
        DateTimeOffset evidenceAt)
    {
        var code = SkillProfileTaxonomy.CreateCode("interview", score.Criterion);
        if (code is null) return;

        evidence.Add(new SkillProfileEvidence(
            identity,
            code,
            SkillProfileTaxonomy.DisplayName(score.Criterion),
            "interview",
            SkillProfileSourceTypes.Interview,
            score.Score,
            evidenceAt));
    }

    private static void AddStarEvidence(
        ICollection<SkillProfileEvidence> evidence,
        StarEvaluation? evaluation,
        string sourceType,
        string identityPrefix,
        DateTimeOffset evidenceAt)
    {
        if (evaluation is null || !evaluation.Applicable ||
            !string.Equals(evaluation.ScoreScale, AiOperations.ScoreScale, StringComparison.Ordinal)) return;

        AddStarComponent(evidence, identityPrefix, "situation", evaluation.Situation, sourceType, evidenceAt);
        AddStarComponent(evidence, identityPrefix, "task", evaluation.Task, sourceType, evidenceAt);
        AddStarComponent(evidence, identityPrefix, "action", evaluation.Action, sourceType, evidenceAt);
        AddStarComponent(evidence, identityPrefix, "result", evaluation.Result, sourceType, evidenceAt);
    }

    private static void AddStarComponent(
        ICollection<SkillProfileEvidence> evidence,
        string identityPrefix,
        string componentName,
        StarComponentEvaluation? component,
        string sourceType,
        DateTimeOffset evidenceAt)
    {
        var validation = StarComponentValidator.Validate(component, componentName);
        var normalized = validation.NormalizedValue;
        if (!validation.IsValid || normalized is null || !normalized.Detected || normalized.Score is < 1 or > 100) return;

        var code = SkillProfileTaxonomy.CreateCode("behavioral", componentName);
        if (code is null) return;

        evidence.Add(new SkillProfileEvidence(
            $"{identityPrefix}:{componentName}",
            code,
            SkillProfileTaxonomy.DisplayName(componentName),
            "behavioral",
            sourceType,
            normalized.Score,
            evidenceAt));
    }

    private static bool TryReadCanonicalRubric(string? json, out IReadOnlyCollection<RubricScore> scores)
    {
        scores = [];
        if (!TryDeserialize(json, out RubricScore?[]? parsed) || parsed is null || parsed.Any(item => item is null)) return false;
        var nonNull = parsed.Select(item => item!).ToArray();
        var validation = CanonicalRubricValidator.ValidateAndNormalize(nonNull);
        if (!validation.IsValid || validation.NormalizedValue is null) return false;
        scores = validation.NormalizedValue;
        return true;
    }

    private static bool TryReadCanonicalRubric(IReadOnlyCollection<RubricScore>? rawScores, out IReadOnlyCollection<RubricScore> scores)
    {
        scores = [];
        var validation = CanonicalRubricValidator.ValidateAndNormalize(rawScores);
        if (!validation.IsValid || validation.NormalizedValue is null) return false;
        scores = validation.NormalizedValue;
        return true;
    }

    private static bool TryReadAnswerEvaluation(string? json, out AnswerEvaluation evaluation)
    {
        if (TryDeserialize(json, out AnswerEvaluation? parsed) && parsed is not null)
        {
            evaluation = parsed;
            return true;
        }

        evaluation = null!;
        return false;
    }

    private static bool TryDeserialize<T>(string? json, out T? value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ResumeAnalysisRow(Guid Id, string Mode, string? Result, DateTimeOffset? CompletedAt, DateTimeOffset UpdatedAt);
    private sealed record ResumeAnalysisSelection(ResumeAnalysisRow Row, ResumeAnalysisOutput Output);
    private sealed record InterviewReportRow(Guid Id, Guid InterviewSessionId, string Rubric, DateTimeOffset CreatedAt);
    private sealed record InterviewAnswerRow(Guid Id, Guid InterviewSessionId, string Evaluation, DateTimeOffset CreatedAt);
    private sealed record StarAttemptRow(Guid Id, string? EvaluationJson, DateTimeOffset? CompletedAt, DateTimeOffset UpdatedAt);
    private sealed record ScenarioAttemptRow(Guid Id, string Competency, string? EvaluationJson, DateTimeOffset? CompletedAt, DateTimeOffset UpdatedAt);
}
