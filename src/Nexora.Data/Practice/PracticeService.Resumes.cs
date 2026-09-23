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

public sealed partial class PracticeService
{
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
