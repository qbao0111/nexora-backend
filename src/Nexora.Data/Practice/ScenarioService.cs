using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class ScenarioService(
    NexoraDbContext dbContext,
    IFeatureEntitlementService featureEntitlementService,
    IAiProvider aiProvider,
    TimeProvider timeProvider) : IScenarioService
{
    private const string ScenarioSchemaVersion = "scenario-v1";
    private const string ScenarioPromptVersion = "scenario-v1";

    private static ScenarioAttemptView MapScenarioAttempt(ScenarioAttempt attempt) =>
        new(attempt.Id, attempt.ScenarioId, attempt.Scenario?.Title ?? "", attempt.Status, attempt.Answer,
            Parse(attempt.EvaluationJson), attempt.ErrorCode, attempt.CreatedAt, attempt.CompletedAt);

    private static JsonElement? Parse(string? value) => string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<JsonElement>(value);

    private string CurrentModelVersion => string.IsNullOrWhiteSpace(aiProvider.ModelVersion)
        ? throw new InvalidOperationException("The configured AI provider must expose a model version.")
        : aiProvider.ModelVersion.Trim();

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
        ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
        : value.Trim();
    private static BusinessException Validation(string message) => new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);

    private static string Fingerprint(params object?[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();

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
        Guid Id, string Status, string? EvaluationJson, string Difficulty, string Competency,
        string CategorySlug, string CategoryName, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt);

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

    private async Task<ScenarioAttempt?> FindScenarioAttemptForUpdateAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        if (dbContext.Database.IsNpgsql())
            return await dbContext.ScenarioAttempts
                .FromSqlInterpolated($"SELECT * FROM scenario_attempts WHERE \"Id\" = {attemptId} FOR UPDATE")
                .Include(item => item.Scenario)
                .SingleOrDefaultAsync(cancellationToken);
        return await dbContext.ScenarioAttempts
            .Include(item => item.Scenario)
            .SingleOrDefaultAsync(item => item.Id == attemptId, cancellationToken);
    }

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null) return null;
        return await dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
    }

    private static async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is null) return;
        await transaction.CommitAsync(cancellationToken);
    }

}
