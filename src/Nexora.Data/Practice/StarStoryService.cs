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

public sealed class StarStoryService(
    NexoraDbContext dbContext,
    IFeatureEntitlementService featureEntitlementService,
    IStructuredAiExecutor structuredAiExecutor,
    TimeProvider timeProvider) : IStarStoryService
{
    private const int MaxTitleLength = 160;
    private const int MaxTagCount = 8;
    private const int MaxTagLength = 40;
    private const int MaxFieldLength = 8_000;
    private const int MaxContentLength = 18_000;
    private const string CreateOperation = "star-story.create";
    private const string EvaluateOperation = "star-story.evaluate";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<StarStoryDetailView> CreateFromAttemptAsync(
        Guid userId,
        StarStoryCreateCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var attempt = await dbContext.StarAttempts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == command.SourceAttemptId && item.UserId == userId, cancellationToken)
            ?? throw StoryNotFound();
        var evaluation = GetUsableEvaluation(attempt);
        var fields = ExtractGroundedFields(evaluation, attempt.Answer);
        var title = NormalizeTitle(command.Title, attempt.Question);
        var tags = NormalizeTags(command.Tags);
        var fingerprint = Fingerprint(command.SourceAttemptId, title, tags);

        var prior = await FindIdempotencyAsync(userId, CreateOperation, key, cancellationToken);
        if (prior is not null)
        {
            EnsureSameFingerprint(prior, fingerprint);
            return await GetAsync(userId, prior.ResourceId, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var story = new StarStory
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = title,
            TagsJson = JsonSerializer.Serialize(tags, JsonOptions),
            Situation = fields.Situation,
            Task = fields.Task,
            Action = fields.Action,
            Result = fields.Result,
            LatestScore = evaluation.OverallScore,
            LatestEvaluationJson = attempt.EvaluationJson,
            LatestModelVersion = attempt.ModelVersion,
            LatestPromptVersion = attempt.PromptVersion,
            LatestSchemaVersion = attempt.SchemaVersion,
            LatestEvaluatedAt = attempt.CompletedAt,
            CreatedAt = now,
            UpdatedAt = now
        };

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await FindIdempotencyAsync(userId, CreateOperation, key, cancellationToken);
        if (prior is not null)
        {
            EnsureSameFingerprint(prior, fingerprint);
            await CommitAsync(transaction, cancellationToken);
            return await GetAsync(userId, prior.ResourceId, cancellationToken);
        }

        dbContext.StarStories.Add(story);
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Operation = CreateOperation,
            Key = key,
            RequestFingerprint = fingerprint,
            ResourceId = story.Id,
            CreatedAt = now
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return Map(story);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            prior = await FindIdempotencyAsync(userId, CreateOperation, key, cancellationToken);
            if (prior is not null)
            {
                EnsureSameFingerprint(prior, fingerprint);
                return await GetAsync(userId, prior.ResourceId, cancellationToken);
            }
            throw;
        }
    }

    public async Task<StarStoryPage> ListAsync(
        Guid userId,
        string? search,
        string? tag,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var query = dbContext.StarStories.AsNoTracking().Where(item => item.UserId == userId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (term.Length > 200) throw Validation("Từ khóa tìm kiếm không hợp lệ.");
            var upperTerm = term.ToUpperInvariant();
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(item => item.Title.ToUpperInvariant().Contains(upperTerm) || item.TagsJson.ToUpperInvariant().Contains(upperTerm) ||
                item.Situation.ToUpperInvariant().Contains(upperTerm) || item.Task.ToUpperInvariant().Contains(upperTerm) ||
                item.Action.ToUpperInvariant().Contains(upperTerm) || item.Result.ToUpperInvariant().Contains(upperTerm));
#pragma warning restore CA1304, CA1311, CA1862
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var normalizedTag = NormalizeTags([tag]).SingleOrDefault();
            if (normalizedTag is not null)
                query = query.Where(item => item.TagsJson.Contains($"\"{normalizedTag}\""));
        }

        var total = await query.CountAsync(cancellationToken);
        var currentPage = Math.Max(1, page ?? 1);
        var currentPageSize = Math.Clamp(pageSize ?? 20, 1, 50);
        var rows = await query.OrderByDescending(item => item.UpdatedAt).ThenByDescending(item => item.Id)
            .Skip((currentPage - 1) * currentPageSize).Take(currentPageSize)
            .Select(item => new StarStoryListRow(item.Id, item.Title, item.TagsJson, item.LatestScore, item.CreatedAt, item.UpdatedAt))
            .ToArrayAsync(cancellationToken);
        return new StarStoryPage(total, rows.Select(item => new StarStoryListItemView(item.Id, item.Title, ParseTags(item.TagsJson),
            item.LatestScore, item.CreatedAt, item.UpdatedAt)).ToArray());
    }

    public async Task<StarStoryDetailView> GetAsync(Guid userId, Guid storyId, CancellationToken cancellationToken) =>
        Map(await FindOwnedStoryAsync(userId, storyId, cancellationToken));

    public async Task<StarStoryDetailView> UpdateAsync(
        Guid userId,
        Guid storyId,
        StarStoryUpdateCommand command,
        CancellationToken cancellationToken)
    {
        var story = await FindOwnedStoryAsync(userId, storyId, cancellationToken);
        var title = NormalizeRequired(command.Title, MaxTitleLength, "Tiêu đề");
        var tags = NormalizeTags(command.Tags);
        var fields = NormalizeFields(command.Situation, command.Task, command.Action, command.Result);
        story.Title = title;
        story.TagsJson = JsonSerializer.Serialize(tags, JsonOptions);
        story.Situation = fields.Situation;
        story.Task = fields.Task;
        story.Action = fields.Action;
        story.Result = fields.Result;
        story.UpdatedAt = NextTimestamp(story.UpdatedAt);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(story);
    }

    public async Task<StarStoryDetailView> EvaluateAsync(Guid userId, Guid storyId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var snapshot = await FindOwnedStoryAsync(userId, storyId, cancellationToken);
        var fingerprint = Fingerprint(storyId, snapshot.UpdatedAt);
        var prior = await FindIdempotencyAsync(userId, EvaluateOperation, key, cancellationToken);
        if (prior is not null)
        {
            EnsureSameFingerprint(prior, fingerprint);
            return await GetAsync(userId, prior.ResourceId, cancellationToken);
        }

        await featureEntitlementService.RequireEnabledAsync(userId, FeatureValues.StarBuilder, cancellationToken);
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.StarBuilder, cancellationToken);
        FeatureReservation? reservation = null;
        var reservationFinalized = false;
        try
        {
            if (access.Limit is not null)
            {
                var reservationKey = $"star-story:{Fingerprint(storyId, key)}";
                reservation = await featureEntitlementService.ReserveAsync(userId, FeatureValues.StarBuilder,
                    reservationKey, reservationKey, cancellationToken);
            }

            var input = BuildEvaluationInput(snapshot);
            var execution = await structuredAiExecutor.ExecuteAsync(
                AiOperations.StarEvaluate,
                input,
                new AiOperationContext(storyId.ToString("N"), userId, ExpectedStar: true),
                cancellationToken);
            if (execution.Value.OverallScore is not { } score || score is < 0 or > 100)
                throw new BusinessException("AI_OUTPUT_INVALID", "AI trả về điểm STAR không hợp lệ.", BusinessErrorKind.ExternalFailure);

            await using var transaction = await BeginTransactionAsync(cancellationToken);
            var story = await dbContext.StarStories.SingleOrDefaultAsync(item => item.Id == storyId && item.UserId == userId, cancellationToken)
                ?? throw StoryNotFound();
            if (story.UpdatedAt != snapshot.UpdatedAt)
                throw new BusinessException("STAR_STORY_CHANGED", "Story đã được chỉnh sửa trong lúc đánh giá. Vui lòng thử lại.", BusinessErrorKind.Conflict);

            var now = NextTimestamp(story.UpdatedAt);
            story.LatestScore = execution.Value.OverallScore;
            story.LatestEvaluationJson = JsonSerializer.Serialize(execution.Value, JsonOptions);
            story.LatestModelVersion = execution.ModelVersion;
            story.LatestPromptVersion = execution.PromptVersion;
            story.LatestSchemaVersion = execution.SchemaVersion;
            story.LatestEvaluatedAt = now;
            story.UpdatedAt = now;
            if (reservation is not null)
            {
                await featureEntitlementService.ConsumeAsync(userId, reservation.EventId, cancellationToken);
                reservationFinalized = true;
            }
            dbContext.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                ActorId = userId,
                Operation = EvaluateOperation,
                Key = key,
                RequestFingerprint = fingerprint,
                ResourceId = storyId,
                CreatedAt = now
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return Map(story);
        }
        catch
        {
            if (reservation is not null && !reservationFinalized)
            {
                try
                {
                    await featureEntitlementService.VoidAsync(userId, reservation.EventId, CancellationToken.None);
                }
                catch
                {
                    // Preserve the original evaluation failure; the usage ledger remains auditable.
                }
            }
            throw;
        }
    }

    private async Task<StarStory> FindOwnedStoryAsync(Guid userId, Guid storyId, CancellationToken cancellationToken) =>
        await dbContext.StarStories.SingleOrDefaultAsync(item => item.Id == storyId && item.UserId == userId, cancellationToken)
        ?? throw StoryNotFound();

    private Task<IdempotencyRecord?> FindIdempotencyAsync(Guid userId, string operation, string key, CancellationToken cancellationToken) =>
        dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == operation && item.Key == key, cancellationToken);

    private static StarEvaluation GetUsableEvaluation(StarAttempt attempt)
    {
        if (attempt.Status != PracticeFeatureValues.Completed || string.IsNullOrWhiteSpace(attempt.EvaluationJson))
            throw NotUsableAttempt();
        StarEvaluation? evaluation;
        try
        {
            evaluation = JsonSerializer.Deserialize<StarEvaluation>(attempt.EvaluationJson, JsonOptions);
        }
        catch (JsonException)
        {
            throw NotUsableAttempt();
        }

        if (evaluation is null || !evaluation.Applicable || !string.Equals(evaluation.ScoreScale, AiOperations.ScoreScale, StringComparison.Ordinal) ||
            evaluation.OverallScore is not { } score || score is < 0 or > 100 ||
            !IsDetected(evaluation.Situation) || !IsDetected(evaluation.Task) ||
            !IsDetected(evaluation.Action) || !IsDetected(evaluation.Result))
            throw NotUsableAttempt();
        return evaluation;
    }

    private static StoryFields ExtractGroundedFields(StarEvaluation evaluation, string answer)
    {
        var fields = new StoryFields(
            evaluation.Situation!.Evidence.Trim(),
            evaluation.Task!.Evidence.Trim(),
            evaluation.Action!.Evidence.Trim(),
            evaluation.Result!.Evidence.Trim());
        if (!ContainsEvidence(answer, fields.Situation) || !ContainsEvidence(answer, fields.Task) ||
            !ContainsEvidence(answer, fields.Action) || !ContainsEvidence(answer, fields.Result))
            throw NotUsableAttempt();
        return NormalizeFields(fields.Situation, fields.Task, fields.Action, fields.Result);
    }

    private static bool IsDetected(StarComponentEvaluation? component) =>
        component is { Detected: true, Score: >= 1 and <= 100 } && !string.IsNullOrWhiteSpace(component.Evidence);

    private static bool ContainsEvidence(string answer, string evidence)
    {
        static string Compact(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return Compact(answer).Contains(Compact(evidence), StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildEvaluationInput(StarStory story) =>
        Bound($"Situation:\n{story.Situation}\n\nTask:\n{story.Task}\n\nAction:\n{story.Action}\n\nResult:\n{story.Result}");

    private static StoryFields NormalizeFields(string situation, string task, string action, string result)
    {
        var normalized = new StoryFields(
            NormalizeRequired(situation, MaxFieldLength, "Situation"),
            NormalizeRequired(task, MaxFieldLength, "Task"),
            NormalizeRequired(action, MaxFieldLength, "Action"),
            NormalizeRequired(result, MaxFieldLength, "Result"));
        if (normalized.Situation.Length + normalized.Task.Length + normalized.Action.Length + normalized.Result.Length > MaxContentLength)
            throw Validation("Tổng nội dung STAR vượt quá giới hạn cho phép.");
        return normalized;
    }

    private static string NormalizeTitle(string? title, string fallback) =>
        string.IsNullOrWhiteSpace(title) ? NormalizeRequired(fallback, MaxTitleLength, "Tiêu đề") : NormalizeRequired(title, MaxTitleLength, "Tiêu đề");

    private static string[] NormalizeTags(IReadOnlyCollection<string>? tags)
    {
        var normalized = (tags ?? []).Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (normalized.Length > MaxTagCount || normalized.Any(tag => tag.Length > MaxTagLength))
            throw Validation($"Story chỉ được có tối đa {MaxTagCount} tag, mỗi tag tối đa {MaxTagLength} ký tự.");
        return normalized;
    }

    private static string[] ParseTags(string? tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson)) return [];
        try { return JsonSerializer.Deserialize<string[]>(tagsJson, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string NormalizeRequired(string? value, int maxLength, string field)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength)
            throw Validation($"{field} không hợp lệ.");
        return normalized;
    }

    private static void EnsureSameFingerprint(IdempotencyRecord prior, string fingerprint)
    {
        if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
            throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
    }

    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
        ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
        : value.Trim();

    private static string Fingerprint(params object?[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();

    private DateTimeOffset NextTimestamp(DateTimeOffset previous)
    {
        var now = timeProvider.GetUtcNow();
        return now > previous ? now : previous.AddTicks(1);
    }

    private static string Bound(string value) => value.Length <= 20_000 ? value : value[..20_000];

    private static StarStoryDetailView Map(StarStory story) =>
        new(story.Id, story.Title, ParseTags(story.TagsJson), story.Situation, story.Task, story.Action, story.Result,
            story.LatestScore, Parse(story.LatestEvaluationJson), story.LatestEvaluatedAt, story.CreatedAt, story.UpdatedAt);

    private static JsonElement? Parse(string? value) => string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<JsonElement>(value, JsonOptions);

    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is not null) return null;
        return await dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);
    }

    private static async Task CommitAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction, CancellationToken cancellationToken)
    {
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);
    }

    private sealed record StoryFields(string Situation, string Task, string Action, string Result);
    private sealed record StarStoryListRow(Guid Id, string Title, string TagsJson, int? LatestScore, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

    private static BusinessException StoryNotFound() =>
        new("STAR_STORY_NOT_FOUND", "Không tìm thấy STAR Story.", BusinessErrorKind.NotFound);

    private static BusinessException NotUsableAttempt() =>
        new("STAR_ATTEMPT_NOT_USABLE", "STAR attempt chưa có đủ nội dung STAR hợp lệ để lưu thành Story.", BusinessErrorKind.Conflict);

    private static BusinessException Validation(string message) =>
        new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);
}
