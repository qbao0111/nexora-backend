using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed partial class ScenarioStarService
{
    public async Task<IReadOnlyCollection<ScenarioCategoryView>> GetCategoriesAsync(CancellationToken cancellationToken) =>
        await dbContext.ScenarioCategories.AsNoTracking().Where(item => item.IsActive).OrderBy(item => item.SortOrder)
            .Select(item => new ScenarioCategoryView(item.Id, item.Slug, item.Name, item.Description, item.SortOrder, item.IsActive)).ToArrayAsync(cancellationToken);

    public async Task<ScenarioPage> GetScenariosAsync(string? category, string? difficulty, string? competency, string? search, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var q = dbContext.Scenarios.AsNoTracking().Include(item => item.Category)
            .Where(item => item.Status == PracticeFeatureValues.Published);
        if (!string.IsNullOrWhiteSpace(category)) q = q.Where(item => item.Category.Slug == category.Trim());
        if (!string.IsNullOrWhiteSpace(difficulty)) q = q.Where(item => item.Difficulty == difficulty.Trim());
        if (!string.IsNullOrWhiteSpace(competency)) q = q.Where(item => item.Competency == competency.Trim());
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{EscapeLikePattern(search.Trim())}%";
            const string escapeCharacter = "\\";
            q = dbContext.Database.IsNpgsql()
                ? q.Where(item => EF.Functions.ILike(item.Title, pattern, escapeCharacter) ||
                                  EF.Functions.ILike(item.Summary, pattern, escapeCharacter))
                : q.Where(item => EF.Functions.Like(item.Title, pattern, escapeCharacter) ||
                                  EF.Functions.Like(item.Summary, pattern, escapeCharacter));
        }
        var total = await q.CountAsync(cancellationToken);
        var p = Math.Max(1, page ?? 1);
        var ps = Math.Clamp(pageSize ?? 20, 1, 50);
        var items = await q.OrderBy(item => item.SortOrder).ThenBy(item => item.Title)
            .Skip((p - 1) * ps).Take(ps)
            .Select(item => new ScenarioCardView(item.Id, item.Slug, item.Title, item.Summary, item.Category.Slug, item.Category.Name, item.Difficulty, item.Competency, item.EstimatedMinutes))
            .ToArrayAsync(cancellationToken);
        return new ScenarioPage(total, items);
    }

    public async Task<ScenarioDetailView> GetScenarioAsync(string slugOrId, CancellationToken cancellationToken)
    {
        var scenario = await dbContext.Scenarios.AsNoTracking().Include(item => item.Category)
            .SingleOrDefaultAsync(item => item.Slug == slugOrId.Trim() || item.Id.ToString() == slugOrId.Trim(), cancellationToken)
            ?? throw new BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy tình huống.", BusinessErrorKind.NotFound);
        if (scenario.Status != PracticeFeatureValues.Published) throw new BusinessException("SCENARIO_NOT_PUBLISHED", "Tình huống chưa được xuất bản.", BusinessErrorKind.NotFound);
        return new ScenarioDetailView(scenario.Id, scenario.Slug, scenario.Title, scenario.Summary, scenario.Category.Slug, scenario.Category.Name,
            scenario.Difficulty, scenario.Competency, scenario.EstimatedMinutes, scenario.Content);
    }

    public async Task<ScenarioAttemptView> CreateAttemptAsync(Guid userId, Guid scenarioId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = scenarioId.ToString("N");
        var scenario = await dbContext.Scenarios.AsNoTracking().SingleOrDefaultAsync(item => item.Id == scenarioId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy tình huống.", BusinessErrorKind.NotFound);
        if (scenario.Status != PracticeFeatureValues.Published) throw new BusinessException("SCENARIO_NOT_PUBLISHED", "Tình huống chưa được xuất bản.", BusinessErrorKind.NotFound);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "scenario-attempt.create" && item.Key == key, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
            return await GetAttemptAsync(userId, prior.ResourceId, cancellationToken);
        }

        var existing = await dbContext.ScenarioAttempts.Include(item => item.Scenario).SingleOrDefaultAsync(
            item => item.UserId == userId && item.ScenarioId == scenarioId && item.Status == PracticeFeatureValues.Draft, cancellationToken);
        if (existing is not null)
        {
            dbContext.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Id = Guid.NewGuid(),
                ActorId = userId,
                Operation = "scenario-attempt.create",
                Key = key,
                RequestFingerprint = fingerprint,
                ResourceId = existing.Id,
                CreatedAt = timeProvider.GetUtcNow()
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapScenarioAttempt(existing);
        }

        var now = timeProvider.GetUtcNow();
        var attempt = new ScenarioAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ScenarioId = scenarioId,
            Status = PracticeFeatureValues.Draft,
            ModelVersion = CurrentModelVersion,
            PromptVersion = ScenarioPromptVersion,
            SchemaVersion = ScenarioSchemaVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.ScenarioAttempts.Add(attempt);
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Operation = "scenario-attempt.create",
            Key = key,
            RequestFingerprint = fingerprint,
            ResourceId = attempt.Id,
            CreatedAt = now
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            attempt.Scenario = scenario;
            return MapScenarioAttempt(attempt);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
                item => item.ActorId == userId && item.Operation == "scenario-attempt.create" && item.Key == key, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
                return await GetAttemptAsync(userId, prior.ResourceId, cancellationToken);
            }
            throw;
        }
    }

    public async Task<ScenarioAttemptView> RetryAttemptAsync(Guid userId, Guid scenarioId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var key = RequireKey(idempotencyKey);
        var fingerprint = scenarioId.ToString("N");
        var scenario = await dbContext.Scenarios.AsNoTracking().SingleOrDefaultAsync(item => item.Id == scenarioId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy tình huống.", BusinessErrorKind.NotFound);
        if (scenario.Status != PracticeFeatureValues.Published)
            throw new BusinessException("SCENARIO_NOT_PUBLISHED", "Tình huống chưa được xuất bản.", BusinessErrorKind.NotFound);

        var prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "scenario-attempt.create" && item.Key == key, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
            return await GetAttemptAsync(userId, prior.ResourceId, cancellationToken);
        }

        var latestQuery = dbContext.ScenarioAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && item.ScenarioId == scenarioId);
        var latest = !dbContext.Database.IsNpgsql()
            ? (await latestQuery.ToArrayAsync(cancellationToken)).OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).FirstOrDefault()
            : await latestQuery.OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).FirstOrDefaultAsync(cancellationToken);
        if (latest is null)
            throw new BusinessException("SCENARIO_ATTEMPT_NOT_FOUND", "Chưa có bài làm để retry.", BusinessErrorKind.NotFound);
        if (latest is not null && latest.Status is PracticeFeatureValues.Draft or PracticeFeatureValues.Queued or PracticeFeatureValues.Processing)
            throw new BusinessException("SCENARIO_ATTEMPT_IN_PROGRESS", "Bài làm hiện tại chưa kết thúc.", BusinessErrorKind.Conflict);

        return await CreateAttemptAsync(userId, scenarioId, key, cancellationToken);
    }

    public async Task<ScenarioAttemptView> SubmitAttemptAsync(Guid userId, Guid attemptId, string answer, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Trim().Length > 12_000) throw Validation("Câu trả lời không hợp lệ.");
        var key = RequireKey(idempotencyKey);
        var fingerprint = Fingerprint(attemptId, answer.Trim());

        var prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "scenario-attempt.submit" && item.Key == key, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
            return await GetAttemptAsync(userId, attemptId, cancellationToken);
        }

        await featureEntitlementService.RequireEnabledAsync(userId, FeatureValues.Scenario, cancellationToken);
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.Scenario, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "scenario-attempt.submit" && item.Key == key, cancellationToken);
        if (prior is not null)
        {
            if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
            return await GetAttemptAsync(userId, attemptId, cancellationToken);
        }

        var attempt = await FindScenarioAttemptForUpdateAsync(attemptId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_ATTEMPT_NOT_FOUND", "Không tìm thấy bài làm.", BusinessErrorKind.NotFound);
        if (attempt.UserId != userId)
            throw new BusinessException("SCENARIO_ATTEMPT_NOT_FOUND", "Không tìm thấy bài làm.", BusinessErrorKind.NotFound);
        if (attempt.Status != PracticeFeatureValues.Draft)
            throw new BusinessException("SCENARIO_ATTEMPT_INVALID_STATE", "Bài làm không ở trạng thái hợp lệ.", BusinessErrorKind.Conflict);

        Guid? reservationId = null;
        if (access.Limit is not null)
        {
            var reservation = await featureEntitlementService.ReserveAsync(userId, FeatureValues.Scenario, attempt.Id.ToString("N"),
                $"scenario:submit:{key}", cancellationToken);
            reservationId = reservation.EventId;
        }

        var now = timeProvider.GetUtcNow();
        attempt.Status = PracticeFeatureValues.Queued;
        attempt.Answer = answer.Trim();
        attempt.UsageReservationId = reservationId;
        attempt.UpdatedAt = now;
        dbContext.OutboxEvents.Add(new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Type = PracticeFeatureValues.ScenarioEvaluationJob,
            AggregateType = "scenario_attempt",
            AggregateId = attempt.Id,
            Payload = JsonSerializer.Serialize(new { attempt.Id, userId }),
            Status = BillingValues.Pending,
            CreatedAt = now
        });
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Operation = "scenario-attempt.submit",
            Key = key,
            RequestFingerprint = fingerprint,
            ResourceId = attempt.Id,
            CreatedAt = now
        });
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapScenarioAttempt(attempt);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            prior = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
                item => item.ActorId == userId && item.Operation == "scenario-attempt.submit" && item.Key == key, cancellationToken);
            if (prior is not null)
            {
                if (!string.Equals(prior.RequestFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
                return await GetAttemptAsync(userId, attemptId, cancellationToken);
            }
            throw;
        }
    }

    public async Task<ScenarioAttemptView> GetAttemptAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken)
    {
        var attempt = await dbContext.ScenarioAttempts.AsNoTracking().Include(item => item.Scenario)
            .SingleOrDefaultAsync(item => item.Id == attemptId && item.UserId == userId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_ATTEMPT_NOT_FOUND", "Không tìm thấy bài làm.", BusinessErrorKind.NotFound);
        return MapScenarioAttempt(attempt);
    }

    public async Task<IReadOnlyCollection<ScenarioAttemptView>> GetAttemptsAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.ScenarioAttempts.AsNoTracking().Include(item => item.Scenario)
            .Where(item => item.UserId == userId).OrderByDescending(item => item.CreatedAt).Take(20)
            .Select(item => new ScenarioAttemptView(item.Id, item.ScenarioId, item.Scenario.Title, item.Status, null,
                Parse(item.EvaluationJson), item.ErrorCode, item.CreatedAt, item.CompletedAt))
            .ToArrayAsync(cancellationToken);

    public async Task<ScenarioAttemptHistoryView> GetAttemptHistoryAsync(Guid userId, string slugOrId, CancellationToken cancellationToken)
    {
        var value = slugOrId.Trim();
        Scenario? scenario = null;
        if (Guid.TryParse(value, out var scenarioId))
            scenario = await dbContext.Scenarios.AsNoTracking().Include(item => item.Category)
                .SingleOrDefaultAsync(item => item.Id == scenarioId, cancellationToken);
        scenario ??= await dbContext.Scenarios.AsNoTracking().Include(item => item.Category)
            .SingleOrDefaultAsync(item => item.Slug == value, cancellationToken);
        if (scenario is null)
            throw new BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy tình huống.", BusinessErrorKind.NotFound);

        var attemptsQuery = dbContext.ScenarioAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && item.ScenarioId == scenario.Id);
        var attempts = !dbContext.Database.IsNpgsql()
            ? (await attemptsQuery.ToArrayAsync(cancellationToken)).OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArray()
            : await attemptsQuery.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToArrayAsync(cancellationToken);
        var previousScore = (int?)null;
        var chronologicalHistory = attempts.Select((attempt, index) =>
        {
            var score = attempt.Status == PracticeFeatureValues.Completed ? ParseScenarioScore(attempt.EvaluationJson) : null;
            var scoreDelta = score.HasValue && previousScore.HasValue ? (int?)(score.Value - previousScore.Value) : null;
            var item = new ScenarioAttemptHistoryItem(
                attempt.Id,
                index + 1,
                attempt.Status,
                attempt.Answer,
                score,
                previousScore,
                scoreDelta,
                scoreDelta.HasValue ? scoreDelta.Value > 0 : null,
                attempt.ErrorCode,
                attempt.CreatedAt,
                attempt.CompletedAt);
            if (score.HasValue) previousScore = score;
            return item;
        }).ToArray();
        var completed = chronologicalHistory.Where(item => item.OverallScore.HasValue).ToArray();
        var latest = completed.LastOrDefault();
        var previous = completed.Length > 1 ? completed[^2] : null;
        var comparison = new ScenarioAttemptComparison(
            latest?.OverallScore,
            previous?.OverallScore,
            latest is not null && previous is not null ? latest.OverallScore!.Value - previous.OverallScore!.Value : null,
            latest is not null && previous is not null ? latest.OverallScore!.Value > previous.OverallScore!.Value : null);

        return new ScenarioAttemptHistoryView(
            scenario.Id,
            scenario.Slug,
            scenario.Title,
            scenario.Category.Slug,
            scenario.Category.Name,
            scenario.Difficulty,
            scenario.Competency,
            chronologicalHistory.Reverse().ToArray(),
            comparison,
            latest?.OverallScore,
            completed.Length == 0 ? null : completed.Max(item => item.OverallScore!.Value));
    }

    public async Task<ScenarioProgressView> GetProgressAsync(Guid userId, CancellationToken cancellationToken)
    {
        var attempts = await dbContext.ScenarioAttempts.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => new ScenarioProgressRow(
                item.Id,
                item.Status,
                item.EvaluationJson,
                item.Scenario.Difficulty,
                item.Scenario.Competency,
                item.Scenario.Category.Slug,
                item.Scenario.Category.Name,
                item.UpdatedAt,
                item.CompletedAt))
            .ToArrayAsync(cancellationToken);
        var progressAttempts = attempts.Select(item => new ScenarioProgressAttempt(item, item.Status == PracticeFeatureValues.Completed
            ? ParseScenarioScore(item.EvaluationJson)
            : null)).ToArray();
        var completed = progressAttempts.Where(item => item.Score.HasValue).ToArray();
        var latest = completed.OrderByDescending(item => item.Attempt.CompletedAt ?? item.Attempt.UpdatedAt)
            .ThenByDescending(item => item.Attempt.Id).FirstOrDefault();

        var tracks = progressAttempts.GroupBy(item => new { item.Attempt.CategorySlug, item.Attempt.CategoryName })
            .Select(group =>
            {
                var scores = group.Where(item => item.Score.HasValue).Select(item => item.Score!.Value).ToArray();
                var latestScore = group.Where(item => item.Score.HasValue)
                    .OrderByDescending(item => item.Attempt.CompletedAt ?? item.Attempt.UpdatedAt).ThenByDescending(item => item.Attempt.Id)
                    .Select(item => item.Score).FirstOrDefault();
                return new ScenarioTrackProgress(group.Key.CategorySlug, group.Key.CategoryName, group.Count(), scores.Length,
                    scores.Length == 0 ? null : (double?)scores.Average(), latestScore);
            })
            .OrderBy(item => item.CategoryName).ToArray();

        var competencies = progressAttempts.GroupBy(item => item.Attempt.Competency)
            .Select(group =>
            {
                var scores = group.Where(item => item.Score.HasValue).Select(item => item.Score!.Value).ToArray();
                var latestScore = group.Where(item => item.Score.HasValue)
                    .OrderByDescending(item => item.Attempt.CompletedAt ?? item.Attempt.UpdatedAt).ThenByDescending(item => item.Attempt.Id)
                    .Select(item => item.Score).FirstOrDefault();
                return new ScenarioCompetencyProgress(group.Key, group.Count(), scores.Length,
                    scores.Length == 0 ? null : (double?)scores.Average(), scores.Length == 0 ? null : scores.Max(), latestScore);
            })
            .OrderBy(item => item.Competency).ToArray();

        var difficulties = progressAttempts.GroupBy(item => NormalizeDifficulty(item.Attempt.Difficulty))
            .Select(group =>
            {
                var scores = group.Where(item => item.Score.HasValue).Select(item => item.Score!.Value).ToArray();
                return new ScenarioDifficultyProgress(group.Key, group.Count(), scores.Length,
                    scores.Length == 0 ? null : (double?)scores.Average());
            })
            .OrderBy(item => DifficultyLevel(item.Difficulty)).ToArray();

        var allScores = completed.Select(item => item.Score).ToArray();
        return new ScenarioProgressView(
            RecommendDifficulty(latest?.Attempt.Difficulty, latest?.Score),
            attempts.Length,
            completed.Length,
            allScores.Length == 0 ? null : (double?)allScores.Average(),
            latest?.Score,
            allScores.Length == 0 ? null : allScores.Max(),
            tracks,
            competencies,
            difficulties);
    }

}
