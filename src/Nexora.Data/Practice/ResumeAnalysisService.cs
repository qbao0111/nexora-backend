using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed class ResumeAnalysisService(
    NexoraDbContext dbContext,
    IAiProvider aiProvider,
    IFeatureEntitlementService featureEntitlementService,
    TimeProvider timeProvider) : IResumeAnalysisService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private string CurrentModelVersion => string.IsNullOrWhiteSpace(aiProvider.ModelVersion)
        ? throw new InvalidOperationException("The configured AI provider must expose a model version.")
        : aiProvider.ModelVersion.Trim();

    private static BusinessException Validation(string message, string code = "VALIDATION_ERROR") => new(code, message, BusinessErrorKind.Validation);
    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private static BusinessException CareerGoalNotFound() => new("CAREER_GOAL_NOT_FOUND", "Không tìm thấy career goal.", BusinessErrorKind.NotFound);
    private static BusinessException Conflict(string code, string message) => new(code, message, BusinessErrorKind.Conflict);
    private static BusinessException InvalidAiOutput() => new("AI_OUTPUT_INVALID", "AI trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128 ? throw Validation("Idempotency-Key hợp lệ là bắt buộc.", "IDEMPOTENCY_KEY_REQUIRED") : value.Trim();
    private static string Fingerprint(params object?[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();
    private static IdempotencyRecord Idempotency(Guid userId, string operation, string key, string fingerprint, Guid resourceId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), ActorId = userId, Operation = operation, Key = key, RequestFingerprint = fingerprint, ResourceId = resourceId, CreatedAt = now };
    private static OutboxEvent Outbox(string type, string aggregateType, Guid aggregateId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), Type = type, AggregateType = aggregateType, AggregateId = aggregateId, Payload = JsonSerializer.Serialize(new { aggregateId }), Status = BillingValues.Pending, CreatedAt = now };

    internal static ResumeAnalysisView MapAnalysis(ResumeAnalysis analysis) => new(
        analysis.Id, analysis.Status, analysis.Result is null ? null : JsonSerializer.Deserialize<JsonElement>(analysis.Result),
        analysis.CreatedAt, analysis.CompletedAt, analysis.ErrorCode, analysis.Mode,
        ParseAnalysisContext(analysis.ContextJson, analysis.Mode), analysis.ResumeVersion, analysis.JobDescriptionVersion,
        analysis.ModelVersion, analysis.PromptVersion, analysis.SchemaVersion, analysis.RubricVersion,
        analysis.ProfileModelVersion, analysis.ProfilePromptVersion, analysis.ProfileSchemaVersion);

    private async Task<IdempotencyRecord?> FindIdempotentAsync(Guid userId, string operation, string key, string fingerprint, CancellationToken cancellationToken)
    {
        var record = await dbContext.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ActorId == userId && item.Operation == operation && item.Key == key, cancellationToken);
        if (record is not null && record.RequestFingerprint != fingerprint)
            throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
        return record;
    }

    private async Task LockUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null) throw NotFound();
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

    private static (int Page, int PageSize) NormalizePaging(int page, int pageSize)
    {
        if (page < 1 || pageSize is < 1 or > 100)
            throw Validation("Tham số phân trang không hợp lệ.", "INVALID_PAGINATION");
        return (page, pageSize);
    }

    private static long PagingSkip(int page, int pageSize) => (long)(page - 1) * pageSize;

    public async Task<ResumeAnalysisView> StartResumeAnalysisAsync(
        Guid userId,
        StartResumeAnalysisCommand command,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = RequireKey(idempotencyKey);
        var normalized = await ResolveResumeAnalysisCommandAsync(userId, command, cancellationToken);
        var fingerprint = Fingerprint(
            normalized.ResumeId,
            normalized.ModeWire,
            normalized.JobDescriptionId,
            normalized.Industry,
            normalized.TargetRole,
            normalized.Seniority);
        var prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);
        var resume = await dbContext.Resumes.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == normalized.ResumeId && item.UserId == userId && item.DeletedAt == null,
            cancellationToken)
            ?? throw NotFound();
        if (resume.Status != PracticeValues.Ready) throw Conflict("RESUME_NOT_READY", "CV chưa sẵn sàng để phân tích.");
        var jd = normalized.JobDescriptionId is null
            ? null
            : await dbContext.JobDescriptions.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == normalized.JobDescriptionId && item.UserId == userId,
                cancellationToken) ?? throw NotFound();
        await featureEntitlementService.RequireEnabledAsync(userId, FeatureValues.CvAnalysis, cancellationToken);
        var access = await featureEntitlementService.GetAsync(userId, FeatureValues.CvAnalysis, cancellationToken);

        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        resume = await dbContext.Resumes.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == normalized.ResumeId && item.UserId == userId && item.DeletedAt == null,
            cancellationToken)
            ?? throw NotFound();
        if (resume.Status != PracticeValues.Ready) throw Conflict("RESUME_NOT_READY", "CV chưa sẵn sàng để phân tích.");
        prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
        if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var operation = GetResumeAnalysisOperation(normalized.Mode);
        var analysis = new ResumeAnalysis
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ResumeId = resume.Id,
            JobDescriptionId = jd?.Id,
            ResumeVersion = resume.Version,
            JobDescriptionVersion = jd?.Version,
            Mode = normalized.ModeWire,
            ContextJson = JsonSerializer.Serialize(
                new ResumeAnalysisContextView(
                    normalized.ModeWire,
                    normalized.Industry,
                    normalized.TargetRole,
                    normalized.Seniority),
                JsonOptions),
            Status = PracticeValues.Queued,
            ModelVersion = CurrentModelVersion,
            PromptVersion = operation.PromptVersion,
            RubricVersion = operation.RubricVersion,
            SchemaVersion = operation.SchemaVersion,
            ProfileModelVersion = resume.ProfileModelVersion,
            ProfilePromptVersion = resume.ProfilePromptVersion,
            ProfileSchemaVersion = resume.ProfileSchemaVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        Guid? reservationId = null;
        if (access.Limit is not null)
        {
            try
            {
                var reservation = await featureEntitlementService.ReserveAsync(userId, FeatureValues.CvAnalysis, analysis.Id.ToString("N"),
                    $"cv-analysis:{key}", cancellationToken);
                reservationId = reservation.EventId;
                analysis.UsageReservationId = reservationId;
            }
            catch (BusinessException ex) when (ex.Code == "IDEMPOTENCY_CONFLICT")
            {
                if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
                dbContext.ChangeTracker.Clear();
                for (var i = 0; i < 10; i++)
                {
                    prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
                    if (prior is not null) break;
                    await Task.Delay(25, cancellationToken);
                }
                if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);
                throw;
            }
        }
        dbContext.AddRange(analysis, Idempotency(userId, "resume-analysis.create", key, fingerprint, analysis.Id, now),
            Outbox("ResumeAnalysisRequested", "resume_analysis", analysis.Id, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapAnalysis(analysis);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            for (var i = 0; i < 10; i++)
            {
                prior = await FindIdempotentAsync(userId, "resume-analysis.create", key, fingerprint, cancellationToken);
                if (prior is not null) break;
                await Task.Delay(25, cancellationToken);
            }
            if (prior is not null) return await GetResumeAnalysisAsync(userId, prior.ResourceId, cancellationToken);
            throw;
        }
    }

    public async Task<ResumeAnalysisView> GetResumeAnalysisAsync(Guid userId, Guid analysisId, CancellationToken cancellationToken)
    {
        var analysis = await dbContext.ResumeAnalyses.AsNoTracking().SingleOrDefaultAsync(item => item.Id == analysisId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        return MapAnalysis(analysis);
    }

    public async Task<ResumeAnalysisHistoryPage> GetResumeAnalysisHistoryAsync(
        Guid userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var paging = NormalizePaging(page, pageSize);
        var query = dbContext.ResumeAnalyses.AsNoTracking().Where(item => item.UserId == userId);
        var totalCount = await query.CountAsync(cancellationToken);
        var skip = PagingSkip(paging.Page, paging.PageSize);
        var projectedQuery = query.Select(item => new
        {
            item.Id,
            item.ResumeId,
            item.Mode,
            item.Status,
            item.CreatedAt,
            item.CompletedAt,
            item.ContextJson,
            item.ErrorCode
        });
        var rows = dbContext.Database.IsNpgsql()
            ? await projectedQuery
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Skip((int)Math.Min(skip, int.MaxValue))
                .Take(paging.PageSize)
                .ToArrayAsync(cancellationToken)
            : (await projectedQuery.ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .Skip((int)Math.Min(skip, int.MaxValue))
                .Take(paging.PageSize)
                .ToArray();

        var items = rows.Select(item => new ResumeAnalysisHistoryItem(
            item.Id,
            item.ResumeId,
            item.Mode,
            item.Status,
            item.CreatedAt,
            item.CompletedAt,
            ParseAnalysisContext(item.ContextJson, item.Mode),
            item.ErrorCode)).ToArray();
        return new(items, paging.Page, paging.PageSize, totalCount, skip + items.Length < totalCount);
    }

    internal static ResumeAnalysisContextView? ParseAnalysisContext(string? value, string mode)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<ResumeAnalysisContextView>(value, JsonOptions);
                if (parsed is not null) return parsed;
            }
            catch (JsonException)
            {
                // Legacy rows have no context snapshot. Keep their mode visible
                // without exposing raw JSON or failing read-only retrieval.
            }
        }

        return string.IsNullOrWhiteSpace(mode)
            ? null
            : new ResumeAnalysisContextView(mode, null, null, null);
    }

    internal static AiOperationDefinition<ResumeAnalysisOutput> GetResumeAnalysisOperation(ResumeAnalysisMode mode) => mode switch
    {
        ResumeAnalysisMode.JobTargeted => AiOperations.ResumeAnalysis,
        ResumeAnalysisMode.FieldBenchmark => AiOperations.ResumeAnalysisFieldBenchmark,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    internal static ResumeAnalysisContextView ReadAnalysisContext(ResumeAnalysis analysis)
    {
        ResumeAnalysisContextView? context;
        if (string.IsNullOrWhiteSpace(analysis.ContextJson))
        {
            // Rows created before CV Analysis v2 have no context snapshot. They
            // are backfilled as job-targeted by the migration and can safely use
            // their persisted job description below.
            context = string.IsNullOrWhiteSpace(analysis.Mode)
                ? null
                : new ResumeAnalysisContextView(analysis.Mode, null, null, null);
        }
        else
        {
            try
            {
                context = JsonSerializer.Deserialize<ResumeAnalysisContextView>(analysis.ContextJson, JsonOptions);
            }
            catch (JsonException)
            {
                context = null;
            }
        }

        if (context is null)
            throw InvalidAiOutput();
        if (!ResumeAnalysisModes.TryParse(context.Mode, out var mode))
            throw Validation("Mode phân tích CV không hợp lệ.", "RESUME_ANALYSIS_MODE_INVALID");
        if (!ResumeAnalysisModes.TryParse(analysis.Mode, out var persistedMode))
            throw Validation("Invalid persisted analysis mode.", "RESUME_ANALYSIS_MODE_INVALID");
        if (persistedMode != mode)
            throw Validation("Persisted analysis context does not match its mode.", "RESUME_ANALYSIS_CONTEXT_INVALID");
        if (mode == ResumeAnalysisMode.JobTargeted)
        {
            if (analysis.JobDescriptionId is null || analysis.JobDescriptionVersion is null || analysis.JobDescription is null ||
                context.Industry is not null || context.TargetRole is not null || context.Seniority is not null)
                throw Validation("Job-targeted analysis context is inconsistent.", "RESUME_ANALYSIS_CONTEXT_INVALID");
        }
        else if (analysis.JobDescriptionId is not null || analysis.JobDescriptionVersion is not null ||
                 string.IsNullOrWhiteSpace(context.Industry) ||
                 string.IsNullOrWhiteSpace(context.TargetRole) ||
                 string.IsNullOrWhiteSpace(context.Seniority))
        {
            throw Validation("Field-benchmark analysis context is inconsistent.", "RESUME_ANALYSIS_CONTEXT_INVALID");
        }
        return context with { Mode = persistedMode.ToWireValue() };
    }

    private async Task<ResolvedResumeAnalysisCommand> ResolveResumeAnalysisCommandAsync(
        Guid userId,
        StartResumeAnalysisCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ResumeId == Guid.Empty)
            throw Validation("Resume không hợp lệ.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        var normalized = NormalizeAnalysisCommand(command);
        ResumeAnalysisGoalContext? goal = null;
        if (command.CareerGoalId is { } careerGoalId)
        {
            goal = await dbContext.CareerGoals.AsNoTracking()
                .Where(item => item.Id == careerGoalId && item.UserId == userId && item.DeletedAt == null)
                .Select(item => new ResumeAnalysisGoalContext(
                    item.TargetRole,
                    item.Seniority,
                    item.Industry,
                    item.TargetJobDescriptionId))
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw CareerGoalNotFound();
        }
        else
        {
            goal = await dbContext.CareerGoals.AsNoTracking()
                .Where(item => item.UserId == userId && item.Active && item.DeletedAt == null)
                .Select(item => new ResumeAnalysisGoalContext(
                    item.TargetRole,
                    item.Seniority,
                    item.Industry,
                    item.TargetJobDescriptionId))
                .SingleOrDefaultAsync(cancellationToken);
        }

        var resumeId = command.ResumeId;
        if (resumeId is null)
        {
            var primaryResumeId = await dbContext.UserProfiles.AsNoTracking()
                .Where(item => item.UserId == userId)
                .Select(item => item.PrimaryResumeId)
                .SingleOrDefaultAsync(cancellationToken);
            if (primaryResumeId is { } selectedResumeId &&
                await dbContext.Resumes.AsNoTracking().AnyAsync(item =>
                    item.Id == selectedResumeId && item.UserId == userId && item.DeletedAt == null,
                    cancellationToken))
                resumeId = selectedResumeId;
            if (resumeId is null || resumeId == Guid.Empty)
                throw NotFound();
        }

        if (normalized.Mode == ResumeAnalysisMode.JobTargeted)
        {
            var jobDescriptionId = normalized.JobDescriptionId ?? goal?.TargetJobDescriptionId;
            if (jobDescriptionId is null)
                throw Validation("Job-targeted analysis yêu cầu JobDescription và không nhận ngữ cảnh benchmark.", "RESUME_ANALYSIS_CONTEXT_INVALID");

            return new(
                normalized.Mode,
                normalized.ModeWire,
                resumeId.Value,
                jobDescriptionId,
                null,
                null,
                null);
        }

        var industry = normalized.Industry ?? NormalizeOptional(goal?.Industry, 160);
        var targetRole = normalized.TargetRole ?? NormalizeOptional(goal?.TargetRole, 160);
        var seniority = normalized.Seniority ?? NormalizeOptional(goal?.Seniority, 80);
        if (industry is null || targetRole is null || seniority is null)
            throw Validation("Field-benchmark analysis yêu cầu industry, targetRole và seniority; không nhận JobDescription.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        return new(
            normalized.Mode,
            normalized.ModeWire,
            resumeId.Value,
            null,
            industry,
            targetRole,
            seniority);
    }

    private static NormalizedResumeAnalysisCommand NormalizeAnalysisCommand(StartResumeAnalysisCommand command)
    {
        if (!ResumeAnalysisModes.TryParse(command.Mode, out var mode))
            throw Validation("Mode phân tích CV không hợp lệ.", "RESUME_ANALYSIS_MODE_INVALID");

        var industry = NormalizeOptional(command.Industry, 160);
        var targetRole = NormalizeOptional(command.TargetRole, 160);
        var seniority = NormalizeOptional(command.Seniority, 80);
        if (command.Industry is not null && industry is null ||
            command.TargetRole is not null && targetRole is null ||
            command.Seniority is not null && seniority is null)
            throw Validation("Ngữ cảnh phân tích CV quá dài.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        if (mode == ResumeAnalysisMode.JobTargeted && (industry is not null || targetRole is not null || seniority is not null))
            throw Validation("Job-targeted analysis yêu cầu JobDescription và không nhận ngữ cảnh benchmark.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        if (mode == ResumeAnalysisMode.FieldBenchmark && command.JobDescriptionId is not null)
            throw Validation("Field-benchmark analysis yêu cầu industry, targetRole và seniority; không nhận JobDescription.", "RESUME_ANALYSIS_CONTEXT_INVALID");

        return new(mode, mode.ToWireValue(), command.JobDescriptionId, industry, targetRole, seniority);
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (value is null) return null;
        var normalized = value.Trim();
        return normalized.Length == 0 || normalized.Length > maxLength ? null : normalized;
    }

    private sealed record NormalizedResumeAnalysisCommand(
        ResumeAnalysisMode Mode,
        string ModeWire,
        Guid? JobDescriptionId,
        string? Industry,
        string? TargetRole,
        string? Seniority);

    private sealed record ResolvedResumeAnalysisCommand(
        ResumeAnalysisMode Mode,
        string ModeWire,
        Guid ResumeId,
        Guid? JobDescriptionId,
        string? Industry,
        string? TargetRole,
        string? Seniority);

    private sealed record ResumeAnalysisGoalContext(
        string TargetRole,
        string Seniority,
        string? Industry,
        Guid? TargetJobDescriptionId);

}
