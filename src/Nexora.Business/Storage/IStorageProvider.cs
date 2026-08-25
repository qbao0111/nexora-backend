namespace Nexora.Business.Storage;

public sealed record StoredObject(string StorageKey, string FileName, string ContentType, long Size);

public interface IStorageProvider
{
    Task<StoredObject> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken);
    Task DeleteAsync(string storageKey, CancellationToken cancellationToken);
}
