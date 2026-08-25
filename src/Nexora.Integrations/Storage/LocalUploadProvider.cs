using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;

namespace Nexora.Integrations.Storage;

public sealed class UploadOptions
{
    public const string SectionName = "Uploads";
    public long MaxResumeBytes { get; set; } = 10 * 1024 * 1024;
    public int IntentMinutes { get; set; } = 10;
}

public sealed class LocalUploadProvider(
    IStorageProvider storageProvider,
    IOptions<UploadOptions> options,
    TimeProvider timeProvider) : IUploadProvider
{
    private sealed record Intent(Guid UserId, string FileName, string ContentType, long Size, DateTimeOffset ExpiresAt)
    {
        public PendingUpload? Completed { get; set; }
    }

    private readonly ConcurrentDictionary<string, Intent> _intents = new(StringComparer.Ordinal);

    public Task<UploadIntent> CreateIntentAsync(Guid userId, string fileName, string contentType, long size, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedType = contentType.Trim().ToLowerInvariant();
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var supported = (extension, normalizedType) is
            (".pdf", "application/pdf") or
            (".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        if (!supported || size <= 0 || size > options.Value.MaxResumeBytes)
            throw InvalidFile();

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(options.Value.IntentMinutes);
        _intents[token] = new Intent(userId, Path.GetFileName(fileName), normalizedType, size, expiresAt);
        return Task.FromResult(new UploadIntent(token, $"/api/v1/uploads/{token}", expiresAt));
    }

    public async Task UploadAsync(string token, Stream content, CancellationToken cancellationToken)
    {
        if (!_intents.TryGetValue(token, out var intent) || intent.ExpiresAt <= timeProvider.GetUtcNow() || intent.Completed is not null)
            throw new BusinessException("UPLOAD_INTENT_INVALID", "Upload intent không hợp lệ hoặc đã hết hạn.", BusinessErrorKind.NotFound);

        await using var buffered = new MemoryStream();
        var buffer = new byte[81920];
        while (buffered.Length <= intent.Size)
        {
            var read = await content.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (buffered.Length != intent.Size || !HasValidSignature(buffered.GetBuffer().AsSpan(0, checked((int)buffered.Length)), intent.ContentType))
            throw InvalidFile();

        buffered.Position = 0;
        var stored = await storageProvider.SaveAsync(buffered, intent.FileName, intent.ContentType, cancellationToken);
        var checksum = Convert.ToHexString(SHA256.HashData(buffered.GetBuffer().AsSpan(0, checked((int)buffered.Length)))).ToLowerInvariant();
        intent.Completed = new PendingUpload(token, intent.UserId, stored.StorageKey, stored.FileName, stored.ContentType, stored.Size, checksum);
    }

    public Task<PendingUpload> GetCompletedAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_intents.TryGetValue(token, out var intent) || intent.UserId != userId || intent.Completed is null)
            throw new BusinessException("UPLOAD_NOT_FOUND", "Không tìm thấy file upload hợp lệ.", BusinessErrorKind.NotFound);
        return Task.FromResult(intent.Completed);
    }

    private static bool HasValidSignature(ReadOnlySpan<byte> content, string contentType) => contentType switch
    {
        "application/pdf" => content.StartsWith("%PDF-"u8),
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => content.StartsWith(new byte[] { 0x50, 0x4B, 0x03, 0x04 }),
        _ => false
    };

    private static BusinessException InvalidFile() =>
        new("INVALID_FILE", "Chỉ chấp nhận file PDF/DOCX hợp lệ trong giới hạn dung lượng.", BusinessErrorKind.Validation);
}
