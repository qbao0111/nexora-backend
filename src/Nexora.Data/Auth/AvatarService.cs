using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nexora.Business.Auth;
using Nexora.Business.Common;
using Nexora.Business.Storage;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.Data.Auth;

public sealed partial class AvatarService(
    NexoraDbContext dbContext,
    IStorageProvider storageProvider,
    TimeProvider timeProvider,
    ILogger<AvatarService> logger) : IAvatarService
{
    private const int MaximumBytes = 2 * 1024 * 1024;

    public async Task<Guid> UploadAsync(
        Guid userId, Stream content, long length, string? contentType, CancellationToken cancellationToken)
    {
        if (length > MaximumBytes)
            throw Validation("AVATAR_FILE_TOO_LARGE", "Ảnh đại diện không được vượt quá 2 MiB.");
        var fileType = NormalizeType(contentType);
        await using var image = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (image.Length + read > MaximumBytes)
                throw Validation("AVATAR_FILE_TOO_LARGE", "Ảnh đại diện không được vượt quá 2 MiB.");
            await image.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (image.Length == 0 || !HasSignature(image.GetBuffer().AsSpan(0, (int)image.Length), fileType))
            throw Validation("AVATAR_FILE_INVALID", "Tệp ảnh đại diện không hợp lệ.");

        image.Position = 0;
        var extension = fileType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            _ => ".webp"
        };
        string? newKey = null;
        string? oldKey = null;
        Guid avatarId;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
            dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken))
        {
            try
            {
                await LockActiveUserAsync(userId, cancellationToken);
                var profile = await dbContext.UserProfiles.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
                var stored = await storageProvider.SaveAsync(image, $"avatar{extension}", fileType, cancellationToken);
                newKey = stored.StorageKey;
                oldKey = profile?.AvatarStorageKey;
                var now = timeProvider.GetUtcNow();
                profile ??= new UserProfile { Id = Guid.NewGuid(), UserId = userId, CreatedAt = now };
                if (dbContext.Entry(profile).State == EntityState.Detached) dbContext.UserProfiles.Add(profile);
                avatarId = Guid.NewGuid();
                profile.AvatarId = avatarId;
                profile.AvatarStorageKey = newKey;
                profile.AvatarContentType = fileType;
                profile.AvatarUpdatedAt = now;
                profile.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                if (newKey is not null) await DeleteBestEffortAsync(newKey);
                throw;
            }
        }
        if (oldKey is not null && oldKey != newKey) await DeleteBestEffortAsync(oldKey);
        return avatarId;
    }

    public async Task DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        string? oldKey;
        await using (var transaction = await dbContext.Database.BeginTransactionAsync(
            dbContext.Database.IsNpgsql() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable,
            cancellationToken))
        {
            await LockActiveUserAsync(userId, cancellationToken);
            var profile = await dbContext.UserProfiles.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            oldKey = profile?.AvatarStorageKey;
            if (profile is not null && profile.AvatarId is not null)
            {
                profile.AvatarId = null;
                profile.AvatarStorageKey = null;
                profile.AvatarContentType = null;
                profile.AvatarUpdatedAt = null;
                profile.UpdatedAt = timeProvider.GetUtcNow();
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        if (oldKey is not null) await DeleteBestEffortAsync(oldKey);
    }

    public async Task<AvatarImage?> OpenAsync(Guid avatarId, CancellationToken cancellationToken)
    {
        var row = await dbContext.UserProfiles.AsNoTracking()
            .Where(item => item.AvatarId == avatarId && item.AvatarStorageKey != null &&
                item.AvatarContentType != null && item.User.IsActive &&
                item.User.DeletionRequestedAt == null && item.User.DeletedAt == null)
            .Select(item => new { item.AvatarStorageKey, item.AvatarContentType })
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        try
        {
            return new AvatarImage(await storageProvider.OpenReadAsync(row.AvatarStorageKey!, cancellationToken), row.AvatarContentType!);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private async Task LockActiveUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = dbContext.Database.IsNpgsql()
            ? await dbContext.Users.FromSqlInterpolated($"SELECT * FROM asp_net_users WHERE \"Id\" = {userId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.Users.SingleOrDefaultAsync(item => item.Id == userId, cancellationToken);
        if (user is null || !user.IsActive || user.DeletionRequestedAt is not null || user.DeletedAt is not null)
            throw new BusinessException("USER_NOT_FOUND", "Không tìm thấy tài khoản.", BusinessErrorKind.NotFound);
    }

    private async Task DeleteBestEffortAsync(string storageKey)
    {
        try { await storageProvider.DeleteAsync(storageKey, CancellationToken.None); }
        catch (Exception exception) { AvatarCleanupFailed(logger, exception.GetType().Name); }
    }

    private static string NormalizeType(string? contentType) => contentType?.Trim().ToLowerInvariant() switch
    {
        "image/jpeg" => "image/jpeg",
        "image/png" => "image/png",
        "image/webp" => "image/webp",
        _ => throw Validation("AVATAR_FILE_TYPE_INVALID", "Chỉ hỗ trợ ảnh JPEG, PNG hoặc WebP.")
    };

    private static bool HasSignature(ReadOnlySpan<byte> bytes, string contentType) => contentType switch
    {
        "image/jpeg" => bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
        "image/png" => bytes.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
        "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };

    private static BusinessException Validation(string code, string message) => new(code, message, BusinessErrorKind.Validation);

    [LoggerMessage(LogLevel.Warning, "Avatar storage cleanup failed: exceptionType={ExceptionType}")]
    private static partial void AvatarCleanupFailed(ILogger logger, string exceptionType);
}
