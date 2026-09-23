using System.Data;
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

public sealed class ResumeService(
    NexoraDbContext dbContext,
    IUploadProvider uploadProvider,
    TimeProvider timeProvider) : IResumeService
{
    private const string ResumeExtractionFailureMessage = "Không thể đọc nội dung CV. Vui lòng thử lại với file PDF hoặc DOCX rõ hơn.";

    internal static ResumeView MapResume(ResumeRecord resume) => new(
        resume.Id, resume.StoredFile.FileName, resume.StoredFile.ContentType, resume.StoredFile.Size,
        resume.Status, resume.CreatedAt,
        resume.Status == PracticeValues.Failed ? "RESUME_EXTRACTION_FAILED" : null,
        resume.Status == PracticeValues.Failed ? ResumeExtractionFailureMessage : null);

    private static BusinessException NotFound() => new("NOT_FOUND", "Không tìm thấy tài nguyên.", BusinessErrorKind.NotFound);
    private static BusinessException Conflict(string code, string message) => new(code, message, BusinessErrorKind.Conflict);

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

    private static OutboxEvent Outbox(string type, string aggregateType, Guid aggregateId, DateTimeOffset now) =>
        new() { Id = Guid.NewGuid(), Type = type, AggregateType = aggregateType, AggregateId = aggregateId, Payload = JsonSerializer.Serialize(new { aggregateId }), Status = BillingValues.Pending, CreatedAt = now };

    private void EnqueueResourceChanged(Guid userId, string resourceType, Guid resourceId, string status, DateTimeOffset occurredAt) =>
        dbContext.RealtimeNotifications.Add(new Nexora.Data.Realtime.RealtimeNotification
        {
            UserId = userId, ResourceType = resourceType, ResourceId = resourceId, Status = status, CreatedAt = occurredAt
        });

    public async Task<ResumeView> CreateResumeAsync(Guid userId, string uploadToken, CancellationToken cancellationToken)
    {
        var upload = await uploadProvider.GetCompletedAsync(userId, uploadToken, cancellationToken);
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);
        var duplicate = await dbContext.StoredFiles.AsNoTracking().SingleOrDefaultAsync(file => file.StorageKey == upload.StorageKey, cancellationToken);
        if (duplicate is not null)
        {
            var existing = await dbContext.Resumes.AsNoTracking().Include(resume => resume.StoredFile)
                .SingleAsync(resume => resume.StoredFileId == duplicate.Id && resume.UserId == userId, cancellationToken);
            if (existing.DeletedAt is not null)
                throw Conflict("RESUME_DELETED", "CV đã bị xóa và không thể khôi phục bằng finalize lại.");
            return MapResume(existing);
        }

        var now = timeProvider.GetUtcNow();
        var storedFile = new StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = upload.StorageKey,
            FileName = upload.FileName,
            ContentType = upload.ContentType,
            Size = upload.Size,
            Checksum = upload.Checksum,
            CreatedAt = now
        };
        var resume = new ResumeRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StoredFileId = storedFile.Id,
            StoredFile = storedFile,
            Status = PracticeValues.Uploaded,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.AddRange(storedFile, resume, Outbox("ResumeExtractionRequested", "resume", resume.Id, now));
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await CommitAsync(transaction, cancellationToken);
            return MapResume(resume);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
            }
            dbContext.ChangeTracker.Clear();
            await using var recoveryTransaction = await BeginTransactionAsync(cancellationToken);
            await LockUserAsync(userId, cancellationToken);
            var concurrent = await dbContext.StoredFiles.AsNoTracking()
                .SingleOrDefaultAsync(file => file.StorageKey == upload.StorageKey && file.UserId == userId, cancellationToken);
            if (concurrent is null) throw;
            var existing = await dbContext.Resumes.AsNoTracking().Include(item => item.StoredFile)
                .SingleOrDefaultAsync(item => item.StoredFileId == concurrent.Id && item.UserId == userId, cancellationToken);
            if (existing is null) throw;
            if (existing.DeletedAt is not null)
                throw Conflict("RESUME_DELETED", "CV đã bị xóa và không thể khôi phục bằng finalize lại.");
            await CommitAsync(recoveryTransaction, cancellationToken);
            return MapResume(existing);
        }
    }

    public async Task DeleteResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken)
    {
        await using var transaction = await BeginTransactionAsync(cancellationToken);
        await LockUserAsync(userId, cancellationToken);

        var resume = await dbContext.Resumes.SingleOrDefaultAsync(
            item => item.Id == resumeId && item.UserId == userId,
            cancellationToken) ?? throw NotFound();
        if (resume.DeletedAt is not null)
        {
            await CommitAsync(transaction, cancellationToken);
            return;
        }

        var now = timeProvider.GetUtcNow();
        resume.DeletedAt = now;
        resume.UpdatedAt = now;
        resume.StorageDeleteNextAttemptAt = now;

        var profile = await dbContext.UserProfiles.SingleOrDefaultAsync(
            item => item.UserId == userId && item.PrimaryResumeId == resumeId,
            cancellationToken);
        if (profile is not null)
        {
            profile.PrimaryResumeId = null;
            profile.UpdatedAt = now;
        }

        EnqueueResourceChanged(userId, "resume", resumeId, "deleted", now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await CommitAsync(transaction, cancellationToken);
    }

    public async Task<ResumeView> GetResumeAsync(Guid userId, Guid resumeId, CancellationToken cancellationToken)
    {
        var resume = await dbContext.Resumes.AsNoTracking().Include(item => item.StoredFile)
            .SingleOrDefaultAsync(item => item.Id == resumeId && item.UserId == userId && item.DeletedAt == null, cancellationToken)
            ?? throw NotFound();
        return MapResume(resume);
    }

    public async Task<IReadOnlyList<ResumeView>> GetResumesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var query = dbContext.Resumes.AsNoTracking().Where(item => item.UserId == userId && item.DeletedAt == null);
        if (string.Equals(dbContext.Database.ProviderName, "Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal))
        {
            return (await query.Select(item => new ResumeView(
                    item.Id,
                    item.StoredFile.FileName,
                    item.StoredFile.ContentType,
                    item.StoredFile.Size,
                    item.Status,
                    item.CreatedAt,
                    item.Status == PracticeValues.Failed ? "RESUME_EXTRACTION_FAILED" : null,
                    item.Status == PracticeValues.Failed ? ResumeExtractionFailureMessage : null))
                .ToArrayAsync(cancellationToken))
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .ToArray();
        }

        return await query
            .OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Select(item => new ResumeView(
                item.Id,
                item.StoredFile.FileName,
                item.StoredFile.ContentType,
                item.StoredFile.Size,
                item.Status,
                item.CreatedAt,
                item.Status == PracticeValues.Failed ? "RESUME_EXTRACTION_FAILED" : null,
                item.Status == PracticeValues.Failed ? ResumeExtractionFailureMessage : null))
            .ToArrayAsync(cancellationToken);
    }

}
