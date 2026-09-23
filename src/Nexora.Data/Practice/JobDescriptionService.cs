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

public sealed class JobDescriptionService(
    NexoraDbContext dbContext,
    TimeProvider timeProvider) : IJobDescriptionService
{
    private const string JobDescriptionCreateOperation = "job-description.create";

    internal static JobDescriptionView MapJobDescription(JobDescription jd) => new(jd.Id, jd.Title, jd.Content, jd.CreatedAt);
    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private static BusinessException Validation(string message, string code = "VALIDATION_ERROR") => new(code, message, BusinessErrorKind.Validation);
    private static string RequireKey(string value) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128 ? throw Validation("Idempotency-Key hợp lệ là bắt buộc.", "IDEMPOTENCY_KEY_REQUIRED") : value.Trim();
    private static string Fingerprint(params object?[] values) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values)))).ToLowerInvariant();
    private static IdempotencyRecord Idempotency(Guid userId, string operation, string key, string fingerprint, Guid resourceId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), ActorId = userId, Operation = operation, Key = key, RequestFingerprint = fingerprint, ResourceId = resourceId, CreatedAt = now };

    private async Task<IdempotencyRecord?> FindIdempotentAsync(Guid userId, string operation, string key, string fingerprint, CancellationToken cancellationToken)
    {
        var record = await dbContext.IdempotencyRecords.AsNoTracking()
            .SingleOrDefaultAsync(item => item.ActorId == userId && item.Operation == operation && item.Key == key, cancellationToken);
        if (record is not null && record.RequestFingerprint != fingerprint)
            throw new BusinessException("IDEMPOTENCY_CONFLICT", "Idempotency-Key đã được dùng với dữ liệu khác.", BusinessErrorKind.Conflict);
        return record;
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

    public async Task<JobDescriptionView> CreateJobDescriptionAsync(
        Guid userId,
        string title,
        string content,
        CancellationToken cancellationToken,
        string? idempotencyKey = null)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 160 || string.IsNullOrWhiteSpace(content) || content.Trim().Length > 30_000)
            throw Validation("Job description không hợp lệ.");
        var normalizedTitle = title.Trim();
        var normalizedContent = content.Trim();
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : RequireKey(idempotencyKey);
        var fingerprint = key is null ? null : Fingerprint(normalizedTitle, normalizedContent);
        if (key is not null)
        {
            var prior = await FindIdempotentAsync(userId, JobDescriptionCreateOperation, key, fingerprint!, cancellationToken);
            if (prior is not null) return await GetJobDescriptionAsync(userId, prior.ResourceId, cancellationToken);
        }

        await using var transaction = key is null ? null : await BeginTransactionAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var jobDescription = new JobDescription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = normalizedTitle,
            Content = normalizedContent,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.JobDescriptions.Add(jobDescription);
        if (key is not null)
            dbContext.IdempotencyRecords.Add(Idempotency(userId, JobDescriptionCreateOperation, key, fingerprint!, jobDescription.Id, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapJobDescription(jobDescription);
        }
        catch (DbUpdateException) when (key is not null)
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                var prior = await FindIdempotentAsync(userId, JobDescriptionCreateOperation, key, fingerprint!, cancellationToken);
                if (prior is not null) return await GetJobDescriptionAsync(userId, prior.ResourceId, cancellationToken);
                await Task.Delay(25, cancellationToken);
            }
            throw;
        }
    }

    public async Task<IReadOnlyList<JobDescriptionView>> GetJobDescriptionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var query = dbContext.JobDescriptions.AsNoTracking().Where(item => item.UserId == userId);
        if (string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            return (await query
                    .Select(item => new JobDescriptionView(item.Id, item.Title, item.Content, item.CreatedAt))
                    .ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .ToArray();
        }

        return await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new JobDescriptionView(item.Id, item.Title, item.Content, item.CreatedAt))
            .ToArrayAsync(cancellationToken);
    }

    public async Task<JobDescriptionView> GetJobDescriptionAsync(Guid userId, Guid jobDescriptionId, CancellationToken cancellationToken)
    {
        var jobDescription = await dbContext.JobDescriptions.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == jobDescriptionId && item.UserId == userId, cancellationToken)
            ?? throw NotFound();
        return MapJobDescription(jobDescription);
    }

}
