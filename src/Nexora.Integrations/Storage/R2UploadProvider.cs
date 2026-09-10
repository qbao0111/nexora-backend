using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;

namespace Nexora.Integrations.Storage;

/// <summary>
/// Durable browser upload flow for R2. Intent state lives in the database through
/// <see cref="IUploadIntentStore"/>; R2 only receives a short-lived private PUT URL.
/// </summary>
internal sealed partial class R2UploadProvider(
    IOptions<R2StorageOptions> r2Options,
    IOptions<UploadOptions> uploadOptions,
    IUploadIntentStore intentStore,
    IR2ObjectClient objectClient,
    IStorageProvider storageProvider,
    UploadDocumentValidator documentValidator,
    TimeProvider timeProvider,
    ILogger<R2UploadProvider>? logger = null) : IUploadProvider
{
    private const int MaximumTokenLength = 128;
    private readonly ILogger<R2UploadProvider> logger = logger ?? NullLogger<R2UploadProvider>.Instance;

    public async Task<UploadIntent> CreateIntentAsync(
        Guid userId,
        string fileName,
        string contentType,
        long size,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NormalizedUploadRequest request;
        try
        {
            request = documentValidator.ValidateRequest(fileName, contentType, size);
        }
        catch (BusinessException exception) when (exception.Kind == BusinessErrorKind.Validation)
        {
            UploadRejected(logger, size, size, uploadOptions.Value.MaxResumeBytes, contentType?.Trim() ?? string.Empty, "unknown", exception.Code);
            throw;
        }
        var intentId = Guid.NewGuid();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var now = timeProvider.GetUtcNow();
        var state = new UploadIntentState(
            intentId,
            userId,
            StorageKeyValidator.Validate($"resumes/{userId:N}/{intentId:N}{Path.GetExtension(request.FileName).ToLowerInvariant()}"),
            request.FileName,
            request.ContentType,
            request.Size,
            now,
            now.AddMinutes(uploadOptions.Value.IntentMinutes),
            Version: 1,
            ActualSize: null,
            Checksum: null,
            CompletedAt: null);

        var created = await intentStore.CreateAsync(state, HashToken(token), cancellationToken);
        return BuildIntent(created, token);
    }

    public Task UploadAsync(string token, Stream content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new BusinessException(
            "UPLOAD_DIRECT_UPLOAD_UNSUPPORTED",
            "R2 upload phải được thực hiện bằng upload URL đã ký.",
            BusinessErrorKind.Conflict);
    }

    public async Task<PendingUpload> GetCompletedAsync(Guid userId, string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedToken = token?.Trim() ?? string.Empty;
        if (normalizedToken.Length is 0 or > MaximumTokenLength)
            throw UploadNotFound();

        var tokenHash = HashToken(normalizedToken);
        var state = await intentStore.FindByTokenHashAsync(userId, tokenHash, cancellationToken);
        if (state is null)
            throw UploadNotFound();
        if (state.IsCompleted)
            return MapCompleted(state, normalizedToken);
        if (state.ExpiresAt <= timeProvider.GetUtcNow())
        {
            UploadRejected(logger, state.ExpectedSize, -1, uploadOptions.Value.MaxResumeBytes, state.ContentType, "unknown", "intent_expired");
            throw new BusinessException("UPLOAD_INTENT_INVALID", "Upload intent không hợp lệ hoặc đã hết hạn.", BusinessErrorKind.NotFound);
        }

        R2ObjectMetadata metadata;
        try
        {
            metadata = await objectClient.GetMetadataAsync(r2Options.Value.Bucket, state.StorageKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw new BusinessException("UPLOAD_OBJECT_NOT_FOUND", "Chưa tìm thấy file upload trên storage.", BusinessErrorKind.NotFound);
        }
        catch (Exception exception)
        {
            throw MapProviderFailure("head", exception);
        }

        if (metadata.Size != state.ExpectedSize)
        {
            UploadRejected(logger, state.ExpectedSize, metadata.Size, uploadOptions.Value.MaxResumeBytes, state.ContentType,
                metadata.ContentType ?? "unknown", "metadata_size");
            throw new BusinessException("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.", BusinessErrorKind.Validation);
        }

        ValidatedUpload validated;
        try
        {
            await using var source = await storageProvider.OpenReadAsync(state.StorageKey, cancellationToken);
            validated = await documentValidator.ValidateAsync(
                source,
                new NormalizedUploadRequest(state.FileName, state.ContentType, state.ExpectedSize),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (BusinessException exception) when (exception.Kind == BusinessErrorKind.Validation)
        {
            UploadRejected(logger, state.ExpectedSize, metadata.Size, uploadOptions.Value.MaxResumeBytes, state.ContentType,
                metadata.ContentType ?? "unknown", exception.Code);
            throw;
        }
        catch (BusinessException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw new BusinessException("UPLOAD_OBJECT_NOT_FOUND", "Chưa tìm thấy file upload trên storage.", BusinessErrorKind.NotFound);
        }
        catch (Exception exception)
        {
            throw MapProviderFailure("validate", exception);
        }

        if (validated.Size != metadata.Size)
        {
            UploadRejected(logger, state.ExpectedSize, validated.Size, uploadOptions.Value.MaxResumeBytes, state.ContentType,
                validated.DetectedContentType, "stream_size");
            throw new BusinessException("UPLOAD_SIZE_MISMATCH", "Kích thước file không khớp với upload intent.", BusinessErrorKind.Validation);
        }

        UploadIntentState? completed;
        try
        {
            completed = await intentStore.CompleteAsync(userId, tokenHash, validated.Size, validated.Checksum, cancellationToken);
        }
        catch (BusinessException exception) when (exception.Kind == BusinessErrorKind.Validation || exception.Kind == BusinessErrorKind.Conflict)
        {
            UploadRejected(logger, state.ExpectedSize, validated.Size, uploadOptions.Value.MaxResumeBytes, state.ContentType,
                validated.DetectedContentType, exception.Code);
            throw;
        }
        if (completed is null)
            throw UploadNotFound();
        return MapCompleted(completed, normalizedToken);
    }

    private UploadIntent BuildIntent(UploadIntentState state, string token)
    {
        if (state.IsCompleted)
            throw new BusinessException(
                "UPLOAD_INTENT_COMPLETED",
                "Upload intent đã được hoàn tất.",
                BusinessErrorKind.Conflict);

        if (state.ExpiresAt <= timeProvider.GetUtcNow())
            throw new BusinessException("UPLOAD_INTENT_EXPIRED", "Upload intent đã hết hạn. Vui lòng tạo yêu cầu mới.", BusinessErrorKind.Conflict);

        try
        {
            var url = objectClient.CreatePresignedPutUrl(
                r2Options.Value.Bucket,
                state.StorageKey,
                state.ContentType,
                state.ExpiresAt);
            return new UploadIntent(token, url, state.ExpiresAt);
        }
        catch (Exception exception)
        {
            throw MapProviderFailure("presign", exception);
        }
    }

    private static PendingUpload MapCompleted(UploadIntentState state, string token) =>
        new(token, state.UserId, state.StorageKey, state.FileName, state.ContentType,
            state.ActualSize ?? state.ExpectedSize, state.Checksum ?? string.Empty);

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private BusinessException MapProviderFailure(string operation, Exception exception)
    {
        ProviderFailure(logger, operation, exception.GetType().Name);
        return new BusinessException(
            "STORAGE_PROVIDER_UNAVAILABLE",
            "Object storage is temporarily unavailable.",
            BusinessErrorKind.ExternalFailure);
    }

    private static BusinessException UploadNotFound() =>
        new("UPLOAD_NOT_FOUND", "Không tìm thấy file upload hợp lệ.", BusinessErrorKind.NotFound);

    [LoggerMessage(LogLevel.Error, "R2 upload provider operation failed: operation={Operation} exceptionType={ExceptionType}")]
    private static partial void ProviderFailure(ILogger logger, string operation, string exceptionType);

    [LoggerMessage(LogLevel.Warning, "R2 upload rejected: expectedBytes={ExpectedBytes} actualBytes={ActualBytes} maxBytes={MaxBytes} declaredType={DeclaredType} detectedType={DetectedType} reason={Reason}")]
    private static partial void UploadRejected(
        ILogger logger,
        long expectedBytes,
        long actualBytes,
        long maxBytes,
        string declaredType,
        string detectedType,
        string reason);
}
