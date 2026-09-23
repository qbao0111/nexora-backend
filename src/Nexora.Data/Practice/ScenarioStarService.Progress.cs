using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Persistence;
using Nexora.Data.Progress;

namespace Nexora.Data.Practice;

public sealed partial class ScenarioStarService
{
    public async Task<ProgressView> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        (await GetProgressAsync(userId, includeSnapshot: false, cancellationToken)).View;

    internal async Task<(ProgressView View, DashboardEvidenceSnapshot Snapshot)> GetDashboardAsync(Guid userId, CancellationToken cancellationToken)
    {
        var result = await GetProgressAsync(userId, includeSnapshot: true, cancellationToken);
        return (result.View, result.Snapshot!);
    }

    private async Task<(ProgressView View, DashboardEvidenceSnapshot? Snapshot)> GetProgressAsync(
        Guid userId, bool includeSnapshot, CancellationToken cancellationToken)
    {
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.ProgressAnalytics, cancellationToken);
        if (!access.Enabled) throw new BusinessException("FEATURE_NOT_AVAILABLE", "Tính năng này không có trong gói hiện tại.", BusinessErrorKind.Forbidden);

        var snapshot = includeSnapshot
            ? await DashboardEvidenceSnapshot.LoadAsync(dbContext, userId, cancellationToken)
            : null;

        var isSqlite = string.Equals(
            dbContext.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.Sqlite",
            StringComparison.Ordinal);
        var completedInterviews = await dbContext.InterviewSessions.AsNoTracking()
            .CountAsync(item => item.UserId == userId && item.Status == PracticeValues.Completed, cancellationToken);
        var recentScoresQuery = dbContext.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId);
        var recentScores = snapshot is not null
            ? snapshot.Reports.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
                .Take(10).Select(item => new RecentInterviewScore(item.InterviewSessionId, item.OverallScore, item.CreatedAt)).ToArray()
            : isSqlite
            ? await dbContext.InterviewReports
                .FromSqlInterpolated($"SELECT * FROM interview_reports WHERE \"UserId\" = {userId} ORDER BY \"CreatedAt\" DESC, \"Id\" DESC LIMIT 10")
                .AsNoTracking()
                .Select(item => new RecentInterviewScore(item.InterviewSessionId, item.OverallScore, item.CreatedAt))
                .ToArrayAsync(cancellationToken)
            : await recentScoresQuery
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Take(10)
                .Select(item => new RecentInterviewScore(item.InterviewSessionId, item.OverallScore, item.CreatedAt))
                .ToArrayAsync(cancellationToken);
        var avgScore = recentScores.Length > 0 ? (double?)recentScores.Average(item => item.Score) : null;

        var starAnswers = snapshot is not null
            ? snapshot.Answers.Select(item => item.Evaluation).ToArray()
            : await dbContext.InterviewAnswers.AsNoTracking()
                .Where(item => item.UserId == userId &&
                               item.EvaluationStatus == InterviewAnswerEvaluationStates.Ready && item.Evaluation != null)
                .Select(item => item.Evaluation!).ToArrayAsync(cancellationToken);
        ProgressStarAverages? starAverages = null;
        var starComponents = starAnswers.SelectMany(eval =>
        {
            try
            {
                var doc = JsonDocument.Parse(eval);
                var star = doc.RootElement.TryGetProperty("star", out var s) ? s : default;
                if (star.ValueKind != JsonValueKind.Object || !star.TryGetProperty("applicable", out var appl) || !appl.GetBoolean()) return [];
                var sit = star.TryGetProperty("situation", out var si) && si.TryGetProperty("score", out var sis) ? sis.GetInt32() : 0;
                var task = star.TryGetProperty("task", out var ta) && ta.TryGetProperty("score", out var tas) ? tas.GetInt32() : 0;
                var act = star.TryGetProperty("action", out var ac) && ac.TryGetProperty("score", out var acs) ? acs.GetInt32() : 0;
                var res = star.TryGetProperty("result", out var re) && re.TryGetProperty("score", out var res1) ? res1.GetInt32() : 0;
                return new[] { (sit: sit, task: task, act: act, res: res) };
            }
            catch { return []; }
        }).ToArray();
        if (starComponents.Length > 0)
        {
            starAverages = new ProgressStarAverages(
                (int)starComponents.Average(x => x.sit),
                (int)starComponents.Average(x => x.task),
                (int)starComponents.Average(x => x.act),
                (int)starComponents.Average(x => x.res));
        }

        var scenarioScores = snapshot is not null
            ? snapshot.Scenarios.Select(item => item.EvaluationJson).ToArray()
            : await dbContext.ScenarioAttempts.AsNoTracking()
                .Where(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed)
                .Select(item => item.EvaluationJson)
                .ToArrayAsync(cancellationToken);
        var completedScenarios = scenarioScores.Length;
        var scoredScenarios = scenarioScores.Select(ParseScenarioScore).Where(score => score.HasValue).ToArray();
        var avgScenarioScore = scoredScenarios.Length > 0
            ? (double?)scoredScenarios.Average(score => score!.Value)
            : null;
        var completedStarAttempts = snapshot is not null
            ? snapshot.Stars.Length
            : await dbContext.StarAttempts.CountAsync(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed, cancellationToken);

        List<RecentActivity> recentActivity;
        if (isSqlite)
        {
            var recentInterviews = await dbContext.InterviewSessions
                .FromSqlInterpolated($"SELECT * FROM interview_sessions WHERE \"UserId\" = {userId} ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT 5")
                .AsNoTracking()
                .Select(item => new { item.Id, item.UpdatedAt })
                .ToArrayAsync(cancellationToken);
            var recentScenarios = await dbContext.ScenarioAttempts
                .FromSqlInterpolated($"SELECT * FROM scenario_attempts WHERE \"UserId\" = {userId} ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT 5")
                .AsNoTracking()
                .Select(item => new { item.Id, item.UpdatedAt })
                .ToArrayAsync(cancellationToken);
            var recentStars = await dbContext.StarAttempts
                .FromSqlInterpolated($"SELECT * FROM star_attempts WHERE \"UserId\" = {userId} ORDER BY \"UpdatedAt\" DESC, \"Id\" DESC LIMIT 5")
                .AsNoTracking()
                .Select(item => new { item.Id, item.UpdatedAt })
                .ToArrayAsync(cancellationToken);
            recentActivity = recentInterviews.Select(item => new RecentActivity("interview", item.Id, item.UpdatedAt))
                .Concat(recentScenarios.Select(item => new RecentActivity("scenario", item.Id, item.UpdatedAt)))
                .Concat(recentStars.Select(item => new RecentActivity("star", item.Id, item.UpdatedAt)))
                .OrderByDescending(item => item.At).Take(10).ToList();
        }
        else
        {
            var recentRows = await dbContext.InterviewSessions.AsNoTracking()
                .Where(item => item.UserId == userId)
                .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id).Take(5)
                .Select(item => new { Kind = 0, item.Id, At = item.UpdatedAt })
                .Concat(dbContext.ScenarioAttempts.AsNoTracking()
                    .Where(item => item.UserId == userId)
                    .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id).Take(5)
                    .Select(item => new { Kind = 1, item.Id, At = item.UpdatedAt }))
                .Concat(dbContext.StarAttempts.AsNoTracking()
                    .Where(item => item.UserId == userId)
                    .OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id).Take(5)
                    .Select(item => new { Kind = 2, item.Id, At = item.UpdatedAt }))
                .ToArrayAsync(cancellationToken);
            recentActivity = recentRows.OrderByDescending(item => item.At)
                .ThenBy(item => item.Kind).ThenByDescending(item => item.Id)
                .Take(10)
                .Select(item => new RecentActivity(item.Kind switch { 0 => "interview", 1 => "scenario", _ => "star" }, item.Id, item.At))
                .ToList();
        }

        return (new ProgressView(completedInterviews, recentScores, avgScore, starAverages, completedScenarios, avgScenarioScore, completedStarAttempts, recentActivity), snapshot);
    }

    private static int? ParseScenarioScore(string? evaluationJson)
    {
        if (string.IsNullOrWhiteSpace(evaluationJson)) return null;
        try
        {
            var doc = JsonDocument.Parse(evaluationJson);
            if (doc.RootElement.TryGetProperty("overallScore", out var score) && score.TryGetInt32(out var value) && value is >= 0 and <= 100)
                return value;
            return null;
        }
        catch { return null; }
    }

    private sealed record ScenarioProgressRow(
        Guid Id,
        string Status,
        string? EvaluationJson,
        string Difficulty,
        string Competency,
        string CategorySlug,
        string CategoryName,
        DateTimeOffset UpdatedAt,
        DateTimeOffset? CompletedAt);

    private sealed record ScenarioProgressAttempt(ScenarioProgressRow Attempt, int? Score);

    private static string RecommendDifficulty(string? currentDifficulty, int? latestScore)
    {
        var currentLevel = DifficultyLevel(currentDifficulty);
        if (latestScore is null) return currentLevel == 0 ? "easy" : NormalizeDifficulty(currentDifficulty);
        return latestScore >= 80 ? DifficultyName(Math.Min(2, currentLevel + 1)) : DifficultyName(currentLevel);
    }

    private static string NormalizeDifficulty(string? difficulty) => DifficultyName(DifficultyLevel(difficulty));

    private static int DifficultyLevel(string? difficulty) => difficulty?.Trim().ToLowerInvariant() switch
    {
        "medium" => 1,
        "hard" => 2,
        _ => 0
    };

    private static string DifficultyName(int level) => level switch
    {
        1 => "medium",
        2 => "hard",
        _ => "easy"
    };

}
