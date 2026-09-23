using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class ScenarioStarService(
    NexoraDbContext dbContext,
    IFeatureEntitlementService featureEntitlementService,
    IAiProvider aiProvider,
    IStructuredAiExecutor structuredAiExecutor,
    TimeProvider timeProvider,
    ILogger<ScenarioStarService> logger) : IScenarioService, IStarAttemptService, IProgressService, IScenarioStarJobProcessor
{
    private const string PromptVersion = "phase3-star-v2";
    private const string SchemaVersion = "phase3-star-v2";
    private const string ScenarioSchemaVersion = "scenario-v1";
    private const string ScenarioPromptVersion = "scenario-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static ScenarioAttemptView MapScenarioAttempt(ScenarioAttempt attempt) =>
        new(attempt.Id, attempt.ScenarioId, attempt.Scenario?.Title ?? "", attempt.Status, attempt.Answer,
            Parse(attempt.EvaluationJson), attempt.ErrorCode, attempt.CreatedAt, attempt.CompletedAt);

    private static StarAttemptView MapStarAttempt(StarAttempt attempt) =>
        new(attempt.Id, attempt.Question, attempt.Answer, attempt.Status, Parse(attempt.EvaluationJson), attempt.ErrorCode, attempt.CreatedAt, attempt.CompletedAt);

    private static JsonElement? Parse(string? value) => string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<JsonElement>(value);

    private void EnqueueResourceChanged(Guid userId, string resourceType, Guid resourceId, string status, DateTimeOffset occurredAt) =>
        dbContext.RealtimeNotifications.Add(new Nexora.Data.Realtime.RealtimeNotification
        {
            UserId = userId,
            ResourceType = resourceType,
            ResourceId = resourceId,
            Status = status,
            CreatedAt = occurredAt
        });

    private string CurrentModelVersion => string.IsNullOrWhiteSpace(aiProvider.ModelVersion)
        ? throw new InvalidOperationException("The configured AI provider must expose a model version.")
        : aiProvider.ModelVersion.Trim();

    private static string Bound(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, 20_000)];
    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
        ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
        : value.Trim();
    private static void MarkProcessed(OutboxEvent job, DateTimeOffset now) { job.Status = BillingValues.Processed; job.ProcessedAt = now; }
    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);
    private static BusinessException InvalidAiOutput() => new("AI_OUTPUT_INVALID", "AI trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
    private static BusinessException Validation(string message) => new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);

    private static string Fingerprint(params object?[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();

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
