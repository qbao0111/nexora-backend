using Microsoft.EntityFrameworkCore;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Persistence;

namespace Nexora.Data.Practice;

public sealed class UploadIntentStore(
    NexoraDbContext dbContext,
    TimeProvider timeProvider) : IUploadIntentStore
{
    public async Task<UploadIntentState?> FindByTokenHashAsync(
        Guid userId,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        var record = await dbContext.UploadIntents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.TokenHash == tokenHash, cancellationToken);
        return record is null ? null : Map(record);
    }

    public async Task<UploadIntentState> CreateAsync(
        UploadIntentState state,
        string tokenHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        dbContext.UploadIntents.Add(new UploadIntentRecord
        {
            Id = state.Id,
            UserId = state.UserId,
            TokenHash = tokenHash,
            StorageKey = state.StorageKey,
            FileName = state.FileName,
            ContentType = state.ContentType,
            ExpectedSize = state.ExpectedSize,
            CreatedAt = state.CreatedAt,
            ExpiresAt = state.ExpiresAt,
            Version = state.Version,
            ActualSize = state.ActualSize,
            Checksum = state.Checksum,
            CompletedAt = state.CompletedAt
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return state;
        }
        catch (DbUpdateException) when (!cancellationToken.IsCancellationRequested)
        {
            dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    public async Task<UploadIntentState?> CompleteAsync(
        Guid userId,
        string tokenHash,
        long actualSize,
        string checksum,
        CancellationToken cancellationToken)
    {
        if (actualSize < 0 || string.IsNullOrWhiteSpace(checksum))
            throw new BusinessException("UPLOAD_FINALIZE_INVALID", "Thông tin file upload không hợp lệ.", BusinessErrorKind.Validation);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = await dbContext.UploadIntents.SingleOrDefaultAsync(
                item => item.UserId == userId && item.TokenHash == tokenHash,
                cancellationToken);
            if (current is null) return null;
            if (current.CompletedAt is not null)
            {
                if (current.ActualSize != actualSize || !string.Equals(current.Checksum, checksum, StringComparison.Ordinal))
                    throw new BusinessException("UPLOAD_FINALIZE_CONFLICT", "Upload intent đã được hoàn tất với object khác.", BusinessErrorKind.Conflict);
                return Map(current);
            }
            var now = timeProvider.GetUtcNow();
            if (current.ExpiresAt <= now)
                throw new BusinessException("UPLOAD_INTENT_INVALID", "Upload intent không hợp lệ hoặc đã hết hạn.", BusinessErrorKind.NotFound);
            if (current.ExpectedSize != actualSize)
                throw new BusinessException("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.", BusinessErrorKind.Validation);

            current.ActualSize = actualSize;
            current.Checksum = checksum;
            current.CompletedAt = now;
            current.Version++;
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return Map(current);
            }
            catch (DbUpdateConcurrencyException)
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        var final = await dbContext.UploadIntents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.TokenHash == tokenHash, cancellationToken);
        if (final is null) return null;
        if (final.CompletedAt is null || final.ActualSize != actualSize ||
            !string.Equals(final.Checksum, checksum, StringComparison.Ordinal))
            throw new BusinessException("UPLOAD_FINALIZE_CONFLICT", "Upload intent đang được hoàn tất với dữ liệu khác.", BusinessErrorKind.Conflict);
        return Map(final);
    }

    private static UploadIntentState Map(UploadIntentRecord record) => new(
        record.Id,
        record.UserId,
        record.StorageKey,
        record.FileName,
        record.ContentType,
        record.ExpectedSize,
        record.CreatedAt,
        record.ExpiresAt,
        record.Version,
        record.ActualSize,
        record.Checksum,
        record.CompletedAt);

}
