namespace Nexora.Business.Storage;

public sealed record StoredObject(string StorageKey, string FileName, string ContentType, long Size);

public interface IStorageProvider
{
    Task<StoredObject> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    /// <summary>Deletes a private object and succeeds when the storage key is already absent.</summary>
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}
