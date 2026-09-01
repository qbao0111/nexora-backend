using System.Data;
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
    TimeProvider timeProvider,
    ILogger<ScenarioStarService> logger) : IScenarioService, IStarAttemptService, IProgressService, IScenarioStarJobProcessor
{
    private const string PromptVersion = "phase3-star-v2";
    private const string SchemaVersion = "phase3-star-v2";
    private const string ScenarioSchemaVersion = "scenario-v1";
    private const string ScenarioPromptVersion = "scenario-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonDocument ScenarioEvaluationSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"overallScore":{"type":"integer"},"dimensions":{"type":"array","items":{"type":"object","properties":{"criterion":{"type":"string"},"score":{"type":"integer"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["criterion","score","evidence","feedback"]}},"strengths":{"type":"array","items":{"type":"string"}},"gaps":{"type":"array","items":{"type":"string"}},"recommendedApproach":{"type":"array","items":{"type":"string"}},"feedback":{"type":"string"}},"required":["overallScore","dimensions","strengths","gaps","recommendedApproach","feedback"]}""");
    private static readonly JsonDocument StarPersonaSituationSchema = JsonDocument.Parse(
        """{"type":"object","properties":{"applicable":{"type":"boolean"},"overallScore":{"type":"integer"},"situation":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"task":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"action":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"result":{"type":"object","properties":{"score":{"type":"integer"},"detected":{"type":"boolean"},"evidence":{"type":"string"},"feedback":{"type":"string"}},"required":["score","detected","evidence","feedback"]},"missingElements":{"type":"array","items":{"type":"string"}},"strengths":{"type":"array","items":{"type":"string"}},"coachingTips":{"type":"array","items":{"type":"string"}}},"required":["applicable"]}""");

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
#pragma warning disable CA1862
            q = q.Where(item => item.Title.ToUpperInvariant().Contains(search.Trim().ToUpperInvariant()) || item.Summary.ToUpperInvariant().Contains(search.Trim().ToUpperInvariant()));
#pragma warning restore CA1862
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
        var scenario = await dbContext.Scenarios.AsNoTracking().SingleOrDefaultAsync(item => item.Id == scenarioId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_NOT_FOUND", "Không tìm thấy tình huống.", BusinessErrorKind.NotFound);
        if (scenario.Status != PracticeFeatureValues.Published) throw new BusinessException("SCENARIO_NOT_PUBLISHED", "Tình huống chưa được xuất bản.", BusinessErrorKind.NotFound);
        var existing = await dbContext.ScenarioAttempts.AsNoTracking().SingleOrDefaultAsync(
            item => item.UserId == userId && item.Status == PracticeFeatureValues.Draft, cancellationToken);
        if (existing is not null) return MapScenarioAttempt(existing);
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
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapScenarioAttempt(attempt);
    }

    public async Task<ScenarioAttemptView> SubmitAttemptAsync(Guid userId, Guid attemptId, string answer, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(answer) || answer.Trim().Length > 12_000) throw Validation("Câu trả lời không hợp lệ.");
        var key = RequireKey(idempotencyKey);
        var attempt = await dbContext.ScenarioAttempts.Include(item => item.Scenario)
            .SingleOrDefaultAsync(item => item.Id == attemptId && item.UserId == userId, cancellationToken)
            ?? throw new BusinessException("SCENARIO_ATTEMPT_NOT_FOUND", "Không tìm thấy bài làm.", BusinessErrorKind.NotFound);
        if (attempt.Status != PracticeFeatureValues.Draft) throw new BusinessException("SCENARIO_ATTEMPT_INVALID_STATE", "Bài làm không ở trạng thái hợp lệ.", BusinessErrorKind.Conflict);

        var existing = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "scenario-attempt.submit" && item.Key == key, cancellationToken);
        if (existing is not null) return await GetAttemptAsync(userId, attempt.Id, cancellationToken);

        await featureEntitlementService.RequireEnabledAsync(userId, FeatureValues.Scenario, cancellationToken);
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.Scenario, cancellationToken);
        Guid? reservationId = null;
        if (access.Limit is not null)
        {
            var reservation = await featureEntitlementService.ReserveAsync(userId, FeatureValues.Scenario, attempt.Id.ToString("N"),
                $"scenario:submit:{key}", cancellationToken);
            reservationId = reservation.EventId;
        }

        attempt.Status = PracticeFeatureValues.Queued;
        attempt.Answer = answer.Trim();
        attempt.UsageReservationId = reservationId;
        attempt.UpdatedAt = timeProvider.GetUtcNow();
        dbContext.OutboxEvents.Add(new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Type = PracticeFeatureValues.ScenarioEvaluationJob,
            AggregateType = "scenario_attempt",
            AggregateId = attempt.Id,
            Payload = JsonSerializer.Serialize(new { attempt.Id, userId }),
            Status = BillingValues.Pending,
            CreatedAt = timeProvider.GetUtcNow()
        });
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Operation = "scenario-attempt.submit",
            Key = key,
            RequestFingerprint = attemptId.ToString("N"),
            ResourceId = attempt.Id,
            CreatedAt = timeProvider.GetUtcNow()
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapScenarioAttempt(attempt);
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

    public async Task<StarAttemptView> CreateAsync(Guid userId, string question, string answer, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Trim().Length > 2_000) throw Validation("Câu hỏi không hợp lệ.");
        if (string.IsNullOrWhiteSpace(answer) || answer.Trim().Length > 12_000) throw Validation("Câu trả lời không hợp lệ.");
        var key = RequireKey(idempotencyKey);
        var existing = await dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(
            item => item.ActorId == userId && item.Operation == "star-attempt.create" && item.Key == key, cancellationToken);
        if (existing is not null) return await GetStarAttemptAsync(userId, existing.ResourceId, cancellationToken);

        await featureEntitlementService.RequireEnabledAsync(userId, FeatureValues.StarBuilder, cancellationToken);
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.StarBuilder, cancellationToken);
        Guid? reservationId = null;
        if (access.Limit is not null)
        {
            var reservation = await featureEntitlementService.ReserveAsync(userId, FeatureValues.StarBuilder, Guid.NewGuid().ToString("N"),
                $"star:create:{key}", cancellationToken);
            reservationId = reservation.EventId;
        }

        var now = timeProvider.GetUtcNow();
        var attempt = new StarAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Question = question.Trim(),
            Answer = answer.Trim(),
            Status = PracticeFeatureValues.Queued,
            UsageReservationId = reservationId,
            ModelVersion = CurrentModelVersion,
            PromptVersion = PromptVersion,
            SchemaVersion = SchemaVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.StarAttempts.Add(attempt);
        dbContext.OutboxEvents.Add(new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Type = PracticeFeatureValues.StarEvaluationJob,
            AggregateType = "star_attempt",
            AggregateId = attempt.Id,
            Payload = JsonSerializer.Serialize(new { attempt.Id, userId }),
            Status = BillingValues.Pending,
            CreatedAt = now
        });
        dbContext.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorId = userId,
            Operation = "star-attempt.create",
            Key = key,
            RequestFingerprint = $"{question.Trim()}:{answer.Trim()}",
            ResourceId = attempt.Id,
            CreatedAt = now
        });
        await dbContext.SaveChangesAsync(cancellationToken);
        return MapStarAttempt(attempt);
    }

    public async Task<StarAttemptView> GetAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken) =>
        await GetStarAttemptAsync(userId, attemptId, cancellationToken);

    public async Task<IReadOnlyCollection<StarAttemptView>> GetManyAsync(Guid userId, CancellationToken cancellationToken) =>
        await dbContext.StarAttempts.AsNoTracking().Where(item => item.UserId == userId).OrderByDescending(item => item.CreatedAt).Take(20)
            .Select(item => new StarAttemptView(item.Id, item.Question, item.Answer, item.Status,
                Parse(item.EvaluationJson), item.ErrorCode, item.CreatedAt, item.CompletedAt))
            .ToArrayAsync(cancellationToken);

    public async Task<ProgressView> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.ProgressAnalytics, cancellationToken);
        if (!access.Enabled) throw new BusinessException("FEATURE_NOT_AVAILABLE", "Tính năng này không có trong gói hiện tại.", BusinessErrorKind.Forbidden);

        var completedInterviews = await dbContext.InterviewSessions.AsNoTracking()
            .CountAsync(item => item.UserId == userId && item.Status == PracticeValues.Completed, cancellationToken);
        var recentScores = await dbContext.InterviewReports.AsNoTracking()
            .Where(item => item.UserId == userId).OrderByDescending(item => item.CreatedAt).Take(10)
            .Select(item => new RecentInterviewScore(item.InterviewSessionId, item.OverallScore, item.CreatedAt)).ToArrayAsync(cancellationToken);
        var avgScore = recentScores.Length > 0 ? (double?)recentScores.Average(item => item.Score) : null;

        var starAnswers = await dbContext.InterviewAnswers.AsNoTracking()
            .Where(item => item.UserId == userId).Select(item => item.Evaluation).ToArrayAsync(cancellationToken);
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

        var completedScenarios = await dbContext.ScenarioAttempts.CountAsync(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed, cancellationToken);
        var scenarioScores = await dbContext.ScenarioAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed && item.EvaluationJson != null)
            .ToArrayAsync(cancellationToken);
        var avgScenarioScore = scenarioScores.Length > 0
            ? (double?)scenarioScores.Select(item => ParseScenarioScore(item.EvaluationJson)).Where(s => s.HasValue).Average(s => s!.Value)
            : null;
        var completedStarAttempts = await dbContext.StarAttempts.CountAsync(item => item.UserId == userId && item.Status == PracticeFeatureValues.Completed, cancellationToken);

        var recentActivity = new List<RecentActivity>();
        var recentInterviews = await dbContext.InterviewSessions.AsNoTracking()
            .Where(item => item.UserId == userId).OrderByDescending(item => item.UpdatedAt).Take(5).ToArrayAsync(cancellationToken);
        foreach (var i in recentInterviews) recentActivity.Add(new RecentActivity("interview", i.Id, i.UpdatedAt));
        var recentScenarioAttempts = await dbContext.ScenarioAttempts.AsNoTracking()
            .Where(item => item.UserId == userId).OrderByDescending(item => item.UpdatedAt).Take(5).ToArrayAsync(cancellationToken);
        foreach (var s in recentScenarioAttempts) recentActivity.Add(new RecentActivity("scenario", s.Id, s.UpdatedAt));
        var recentStars = await dbContext.StarAttempts.AsNoTracking()
            .Where(item => item.UserId == userId).OrderByDescending(item => item.UpdatedAt).Take(5).ToArrayAsync(cancellationToken);
        foreach (var st in recentStars) recentActivity.Add(new RecentActivity("star", st.Id, st.UpdatedAt));
        recentActivity = recentActivity.OrderByDescending(item => item.At).Take(10).ToList();

        return new ProgressView(completedInterviews, recentScores, avgScore, starAverages, completedScenarios, avgScenarioScore, completedStarAttempts, recentActivity);
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var staleBefore = now.AddMinutes(-10);
        var relevant = dbContext.OutboxEvents.AsNoTracking().Where(item =>
            item.Type == PracticeFeatureValues.ScenarioEvaluationJob || item.Type == PracticeFeatureValues.StarEvaluationJob);
        var candidates = !dbContext.Database.IsNpgsql()
            ? (await relevant.ToArrayAsync(cancellationToken))
                .Where(item => item.Status == BillingValues.Pending ||
                    (item.Status == BillingValues.Processing && item.ProcessedAt <= staleBefore))
                .OrderBy(item => item.CreatedAt).Take(20).ToArray()
            : await relevant.Where(item => item.Status == BillingValues.Pending ||
                    (item.Status == BillingValues.Processing && item.ProcessedAt <= staleBefore))
                .OrderBy(item => item.CreatedAt).Take(20).ToArrayAsync(cancellationToken);
        var claimedCount = 0;
        foreach (var candidate in candidates)
        {
            var claimQuery = dbContext.OutboxEvents.Where(item => item.Id == candidate.Id && item.Status == candidate.Status);
            claimQuery = candidate.Status == BillingValues.Pending
                ? claimQuery.Where(item => item.ProcessedAt == null)
                : claimQuery.Where(item => item.ProcessedAt == candidate.ProcessedAt);
            var claimed = await claimQuery.ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.Status, BillingValues.Processing)
                .SetProperty(item => item.ProcessedAt, now), cancellationToken);
            if (claimed == 0) continue;

            claimedCount++;
            var job = await dbContext.OutboxEvents.SingleAsync(item => item.Id == candidate.Id, cancellationToken);
            try
            {
                switch (job.Type)
                {
                    case "ScenarioEvaluationRequested": await EvaluateScenarioAsync(job, cancellationToken); break;
                    case "StarEvaluationRequested": await EvaluateStarAsync(job, cancellationToken); break;
                }
                JobCompleted(logger, job.Id, job.Type, job.AggregateId);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                await FailJobAsync(job, exception, cancellationToken);
                JobFailed(logger, job.Id, job.Type, job.AggregateId, exception.GetType().Name);
            }
        }
        return claimedCount;
    }

    private async Task EvaluateScenarioAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var attempt = await dbContext.ScenarioAttempts.Include(item => item.Scenario)
            .SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        attempt.Status = PracticeFeatureValues.Processing;
        attempt.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        var input = $"Scenario: {attempt.Scenario.Title}\nCompetency: {attempt.Scenario.Competency}\nDifficulty: {attempt.Scenario.Difficulty}\n\nContent:\n{attempt.Scenario.Content}\n\nUser Answer:\n{attempt.Answer}";
        try
        {
            var result = await aiProvider.GenerateStructuredAsync<ScenarioEvaluationResult>(
                new AiRequest("scenario.evaluate", ScenarioPromptVersion, CurrentModelVersion, ScenarioSchemaVersion, ScenarioSchemaVersion,
                    Bound(input), ScenarioEvaluationSchema, 2_000, attempt.Id.ToString("N")), cancellationToken);
            if (result.OverallScore < 0 || result.OverallScore > 100 || result.Dimensions.Count == 0) throw InvalidAiOutput();
            foreach (var d in result.Dimensions)
                if (d.Score < 0 || d.Score > 100 || string.IsNullOrWhiteSpace(d.Evidence) || string.IsNullOrWhiteSpace(d.Feedback)) throw InvalidAiOutput();

            attempt.EvaluationJson = JsonSerializer.Serialize(result, JsonOptions);
            attempt.Status = PracticeFeatureValues.Completed;
            attempt.CompletedAt = attempt.UpdatedAt = timeProvider.GetUtcNow();
            MarkProcessed(job, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);

            if (attempt.UsageReservationId.HasValue)
                await featureEntitlementService.ConsumeAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
        }
        catch (Exception) when (attempt.UsageReservationId.HasValue && !cancellationToken.IsCancellationRequested)
        {
            if (attempt.UsageReservationId.HasValue)
                await featureEntitlementService.VoidAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
            throw;
        }
    }

    private async Task EvaluateStarAsync(OutboxEvent job, CancellationToken cancellationToken)
    {
        var attempt = await dbContext.StarAttempts.SingleAsync(item => item.Id == job.AggregateId, cancellationToken);
        attempt.Status = PracticeFeatureValues.Processing;
        attempt.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken);

        var input = $"Question: {attempt.Question}\n\nAnswer: {attempt.Answer}";
        try
        {
            var evaluation = await aiProvider.GenerateStructuredAsync<StarEvaluation>(
                new AiRequest("star.evaluate", PromptVersion, CurrentModelVersion, PromptVersion, SchemaVersion,
                    Bound(input), StarPersonaSituationSchema, 2_000, attempt.Id.ToString("N")), cancellationToken);
            evaluation = ValidateAndNormalizeStar(evaluation, attempt.Question);

            attempt.EvaluationJson = JsonSerializer.Serialize(evaluation, JsonOptions);
            attempt.Status = PracticeFeatureValues.Completed;
            attempt.CompletedAt = attempt.UpdatedAt = timeProvider.GetUtcNow();
            MarkProcessed(job, timeProvider.GetUtcNow());
            await dbContext.SaveChangesAsync(cancellationToken);

            if (attempt.UsageReservationId.HasValue)
                await featureEntitlementService.ConsumeAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
        }
        catch (Exception) when (attempt.UsageReservationId.HasValue && !cancellationToken.IsCancellationRequested)
        {
            if (attempt.UsageReservationId.HasValue)
                await featureEntitlementService.VoidAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
            throw;
        }
    }

    private async Task FailJobAsync(OutboxEvent job, Exception exception, CancellationToken cancellationToken)
    {
        dbContext.ChangeTracker.Clear();
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        var current = await dbContext.OutboxEvents.SingleAsync(item => item.Id == job.Id, cancellationToken);
        current.Status = PracticeFeatureValues.Failed;
        current.ProcessedAt = timeProvider.GetUtcNow();

        if (current.Type == PracticeFeatureValues.ScenarioEvaluationJob)
        {
            var attempt = await dbContext.ScenarioAttempts.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            attempt.Status = PracticeFeatureValues.Failed;
            attempt.ErrorCode = "AI_PROCESSING_FAILED";
            attempt.UpdatedAt = current.ProcessedAt.Value;
            if (attempt.UsageReservationId.HasValue)
                await featureEntitlementService.VoidAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
        }
        else if (current.Type == PracticeFeatureValues.StarEvaluationJob)
        {
            var attempt = await dbContext.StarAttempts.SingleAsync(item => item.Id == current.AggregateId, cancellationToken);
            attempt.Status = PracticeFeatureValues.Failed;
            attempt.ErrorCode = "AI_PROCESSING_FAILED";
            attempt.UpdatedAt = current.ProcessedAt.Value;
            if (attempt.UsageReservationId.HasValue)
                await featureEntitlementService.VoidAsync(attempt.UserId, attempt.UsageReservationId.Value, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<StarAttemptView> GetStarAttemptAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken)
    {
        var attempt = await dbContext.StarAttempts.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == attemptId && item.UserId == userId, cancellationToken)
            ?? throw new BusinessException("STAR_ATTEMPT_NOT_FOUND", "Không tìm thấy STAR attempt.", BusinessErrorKind.NotFound);
        return MapStarAttempt(attempt);
    }

    private static StarEvaluation ValidateAndNormalizeStar(StarEvaluation star, string question)
    {
        if (!star.Applicable) return star with { OverallScore = null, Situation = null, Task = null, Action = null, Result = null, MissingElements = [], Strengths = [], CoachingTips = [] };
        var sit = ValidateComponent(star.Situation, "situation");
        var task = ValidateComponent(star.Task, "task");
        var act = ValidateComponent(star.Action, "action");
        var res = ValidateComponent(star.Result, "result");
        var missing = new[] { ("situation", sit), ("task", task), ("action", act), ("result", res) }
            .Where(x => !x.Item2.Detected || x.Item2.Score < 60).Select(x => x.Item1)
            .Concat(star.MissingElements ?? []).Distinct(StringComparer.Ordinal).Take(4).ToArray();
        return star with
        {
            OverallScore = (int)Math.Round(sit.Score * .20 + task.Score * .20 + act.Score * .35 + res.Score * .25),
            Situation = sit, Task = task, Action = act, Result = res,
            MissingElements = missing,
            Strengths = star.Strengths?.Where(NotBlank).Take(3).ToArray() ?? [],
            CoachingTips = star.CoachingTips?.Where(NotBlank).Take(3).ToArray() ?? []
        };
    }

    private static StarComponentEvaluation ValidateComponent(StarComponentEvaluation? component, string name)
    {
        if (component is null || component.Score is < 0 or > 100 || string.IsNullOrWhiteSpace(component.Feedback))
            throw InvalidAiOutput();
        return component;
    }

    private static int? ParseScenarioScore(string? evaluationJson)
    {
        if (string.IsNullOrWhiteSpace(evaluationJson)) return null;
        try
        {
            var doc = JsonDocument.Parse(evaluationJson);
            if (doc.RootElement.TryGetProperty("overallScore", out var score)) return score.GetInt32();
            return null;
        }
        catch { return null; }
    }

    private static ScenarioAttemptView MapScenarioAttempt(ScenarioAttempt attempt) =>
        new(attempt.Id, attempt.ScenarioId, attempt.Scenario?.Title ?? "", attempt.Status, attempt.Answer,
            Parse(attempt.EvaluationJson), attempt.ErrorCode, attempt.CreatedAt, attempt.CompletedAt);

    private static StarAttemptView MapStarAttempt(StarAttempt attempt) =>
        new(attempt.Id, attempt.Question, attempt.Answer, attempt.Status, Parse(attempt.EvaluationJson), attempt.ErrorCode, attempt.CreatedAt, attempt.CompletedAt);

    private static JsonElement? Parse(string? value) => string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<JsonElement>(value);

    private string CurrentModelVersion => string.IsNullOrWhiteSpace(aiProvider.ModelVersion)
        ? throw new InvalidOperationException("The configured AI provider must expose a model version.")
        : aiProvider.ModelVersion.Trim();

    private static string Bound(string? value) => string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, 20_000)];
    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128
        ? throw new BusinessException("IDEMPOTENCY_KEY_REQUIRED", "Idempotency-Key hợp lệ là bắt buộc.", BusinessErrorKind.Validation)
        : value.Trim();
    private static void MarkProcessed(OutboxEvent job, DateTimeOffset now) { job.Status = BillingValues.Processed; job.ProcessedAt = now; }
    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);
    private static BusinessException InvalidAiOutput() => new("AI_OUTPUT_INVALID", "AI trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
    private static BusinessException Validation(string message) => new("VALIDATION_ERROR", message, BusinessErrorKind.Validation);
    private Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        dbContext.Database.BeginTransactionAsync(dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable, cancellationToken);

    [LoggerMessage(LogLevel.Information, "Job {JobId} ({JobType}/{AggregateId}) completed")]
    private static partial void JobCompleted(ILogger logger, Guid jobId, string jobType, Guid aggregateId);
    [LoggerMessage(LogLevel.Error, "Job {JobId} ({JobType}/{AggregateId}) failed with {ExceptionType}")]
    private static partial void JobFailed(ILogger logger, Guid jobId, string jobType, Guid aggregateId, string exceptionType);
}