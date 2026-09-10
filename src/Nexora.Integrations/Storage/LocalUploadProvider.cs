using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

public sealed partial class LocalUploadProvider(
    IStorageProvider storageProvider,
    IOptions<UploadOptions> options,
    TimeProvider timeProvider,
    ILogger<LocalUploadProvider>? logger = null,
    UploadDocumentValidator? documentValidator = null) : IUploadProvider
{
    private const long HardMaximumResumeBytes = 25 * 1024 * 1024;

    private readonly ILogger<LocalUploadProvider> logger = logger ?? NullLogger<LocalUploadProvider>.Instance;
    private readonly UploadDocumentValidator documentValidator = documentValidator ?? new(options);

    private sealed class Intent(
        Guid userId,
        string fileName,
        string contentType,
        long size,
        DateTimeOffset expiresAt)
    {
        public Guid UserId { get; } = userId;
        public string FileName { get; } = fileName;
        public string ContentType { get; } = contentType;
        public long Size { get; } = size;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public PendingUpload? Completed { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    private readonly ConcurrentDictionary<string, Intent> intents = new(StringComparer.Ordinal);

    public Task<UploadIntent> CreateIntentAsync(
        Guid userId,
        string fileName,
        string contentType,
        long size,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var maxBytes = GetMaximumResumeBytes();
        NormalizedUploadRequest request;
        try
        {
            request = documentValidator.ValidateRequest(fileName, contentType, size);
        }
        catch (BusinessException exception) when (exception.Kind == BusinessErrorKind.Validation)
        {
            throw Reject(exception.Code, exception.Message, size, size, maxBytes, contentType?.Trim() ?? string.Empty, "unknown", exception.Code);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var expiresAt = timeProvider.GetUtcNow().AddMinutes(options.Value.IntentMinutes);
        intents[token] = new Intent(userId, request.FileName, request.ContentType, request.Size, expiresAt);
        return Task.FromResult(new UploadIntent(token, $"/api/v1/uploads/{token}", expiresAt));
    }

    public async Task UploadAsync(string token, Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var maxBytes = GetMaximumResumeBytes();

        if (!intents.TryGetValue(token, out var intent))
            throw Reject("UPLOAD_INTENT_INVALID", "Upload intent không hợp lệ hoặc đã hết hạn.", -1, -1, maxBytes, "unknown", "unknown", "intent_missing");

        await intent.Gate.WaitAsync(cancellationToken);
        try
        {
            if (intent.ExpiresAt <= timeProvider.GetUtcNow() || intent.Completed is not null)
                throw Reject("UPLOAD_INTENT_INVALID", "Upload intent không hợp lệ hoặc đã hết hạn.", intent.Size, -1, maxBytes, intent.ContentType, "unknown", "intent_expired_or_used");

            ValidatedUpload validated;
            try
            {
                validated = await documentValidator.ValidateAsync(
                    content,
                    new NormalizedUploadRequest(intent.FileName, intent.ContentType, intent.Size),
                    cancellationToken);
            }
            catch (BusinessException exception) when (exception.Kind == BusinessErrorKind.Validation)
            {
                throw Reject(exception.Code, exception.Message, intent.Size, -1, maxBytes, intent.ContentType, "unknown", exception.Code);
            }

            await using var source = new MemoryStream(validated.Content, writable: false);
            var stored = await storageProvider.SaveAsync(source, intent.FileName, intent.ContentType, cancellationToken);
            if (stored.Size != intent.Size)
            {
                await TryDeleteAsync(stored.StorageKey, cancellationToken);
                throw Reject("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.", intent.Size, stored.Size, maxBytes, intent.ContentType, validated.DetectedContentType, "stored_size");
            }

            // The per-intent gate makes the capability single-use even when two PUTs
            // arrive concurrently; only the winning upload can create a stored object.
            intent.Completed = new PendingUpload(
                token,
                intent.UserId,
                stored.StorageKey,
                stored.FileName,
                stored.ContentType,
                stored.Size,
                validated.Checksum);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            LogUploadRejected(logger, intent.Size, -1, maxBytes, intent.ContentType, "unknown", "cancelled");
            throw;
        }
        finally
        {
            intent.Gate.Release();
        }
    }

    public Task<PendingUpload> GetCompletedAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!intents.TryGetValue(token, out var intent) || intent.UserId != userId || intent.Completed is null)
            throw new BusinessException("UPLOAD_NOT_FOUND", "Không tìm thấy file upload hợp lệ.", BusinessErrorKind.NotFound);
        return Task.FromResult(intent.Completed);
    }

    private async Task TryDeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        try
        {
            await storageProvider.DeleteAsync(storageKey, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStoredObjectCleanupFailed(logger, exception.GetType().Name);
        }
    }

    private long GetMaximumResumeBytes() =>
        options.Value.MaxResumeBytes is > 0 and <= HardMaximumResumeBytes
            ? options.Value.MaxResumeBytes
            : throw new BusinessException("UPLOAD_CONFIGURATION_INVALID", "Giới hạn upload chưa được cấu hình hợp lệ.", BusinessErrorKind.ExternalFailure);

    private BusinessException Reject(
        string code,
        string message,
        long expectedSize,
        long actualSize,
        long maxBytes,
        string declaredType,
        string detectedType,
        string reason)
    {
        LogUploadRejected(logger, expectedSize, actualSize, maxBytes, declaredType, detectedType, reason);
        return new BusinessException(code, message, code == "UPLOAD_INTENT_INVALID" ? BusinessErrorKind.NotFound : BusinessErrorKind.Validation);
    }

    [LoggerMessage(LogLevel.Warning, "Upload rejected: expectedBytes={ExpectedBytes} actualBytes={ActualBytes} maxBytes={MaxBytes} declaredType={DeclaredType} detectedType={DetectedType} reason={Reason}")]
    private static partial void LogUploadRejected(
        ILogger logger,
        long expectedBytes,
        long actualBytes,
        long maxBytes,
        string declaredType,
        string detectedType,
        string reason);

    [LoggerMessage(LogLevel.Warning, "Stored upload cleanup failed after validation: exceptionType={ExceptionType}")]
    private static partial void LogStoredObjectCleanupFailed(ILogger logger, string exceptionType);

}
