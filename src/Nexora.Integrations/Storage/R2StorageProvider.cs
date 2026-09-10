using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Storage;

namespace Nexora.Integrations.Storage;

public sealed partial class R2StorageProvider : IStorageProvider
{
    private readonly R2StorageOptions options;
    private readonly IR2ObjectClient client;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<R2StorageProvider> logger;

    internal R2StorageProvider(
        IOptions<R2StorageOptions> options,
        IR2ObjectClient client,
        TimeProvider timeProvider,
        ILogger<R2StorageProvider>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(timeProvider);
        this.options = options.Value;
        this.client = client;
        this.timeProvider = timeProvider;
        this.logger = logger ?? NullLogger<R2StorageProvider>.Instance;
    }

    public async Task<StoredObject> SaveAsync(
        Stream content,
        string fileName,
        string contentType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var normalizedFileName = Path.GetFileName(fileName.Trim());
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedFileName);
        var normalizedContentType = contentType.Trim();
        var storageKey = CreateStorageKey(normalizedFileName);
        try
        {
            var size = await client.PutObjectAsync(
                options.Bucket,
                storageKey,
                content,
                normalizedContentType,
                cancellationToken);
            return new StoredObject(storageKey, normalizedFileName, normalizedContentType, size);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapFailure("write", exception);
        }
    }

    public async Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        var validatedKey = StorageKeyValidator.Validate(storageKey);
        try
        {
            return await client.OpenReadAsync(options.Bucket, validatedKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapFailure("open", exception);
        }
    }

    public async Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        var validatedKey = StorageKeyValidator.Validate(storageKey);
        try
        {
            await client.DeleteObjectAsync(options.Bucket, validatedKey, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapFailure("delete", exception);
        }
    }

    private string CreateStorageKey(string fileName)
    {
        var now = timeProvider.GetUtcNow();
        var extension = Path.GetExtension(fileName);
        return StorageKeyValidator.Validate(
            $"{now:yyyy}/{now:MM}/{Guid.NewGuid():N}{extension}");
    }

    private BusinessException MapFailure(string operation, Exception exception)
    {
        StorageOperationFailed(logger, operation, exception.GetType().Name);
        return new BusinessException(
            "STORAGE_PROVIDER_UNAVAILABLE",
            "Object storage is temporarily unavailable.",
            BusinessErrorKind.ExternalFailure);
    }

    [LoggerMessage(LogLevel.Error, "R2 storage operation failed: operation={Operation} exceptionType={ExceptionType}")]
    private static partial void StorageOperationFailed(ILogger logger, string operation, string exceptionType);
}
