using Microsoft.Extensions.Options;
using Nexora.Business.Storage;

namespace Nexora.Integrations.Storage;

public sealed class LocalStorageProvider : IStorageProvider
{
    private readonly string _rootPath;
    private readonly TimeProvider _timeProvider;

    public LocalStorageProvider(IOptions<LocalStorageOptions> options, TimeProvider timeProvider)
    {
        _rootPath = Path.GetFullPath(options.Value.RootPath);
        _timeProvider = timeProvider;
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<StoredObject> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        var now = _timeProvider.GetUtcNow();
        var storageKey = Path.Combine(
                now.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
                now.ToString("MM", System.Globalization.CultureInfo.InvariantCulture),
                $"{Guid.NewGuid():N}{Path.GetExtension(fileName)}")
            .Replace(Path.DirectorySeparatorChar, '/');
        var fullPath = ResolvePrivatePath(storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var fileCreated = false;
        try
        {
            long storedLength;
            await using (var destination = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                fileCreated = true;
                await content.CopyToAsync(destination, cancellationToken);
                storedLength = destination.Length;
            }
            return new StoredObject(storageKey, Path.GetFileName(fileName), contentType, storedLength);
        }
        catch
        {
            // A cancelled/failed stream must not leave an orphaned private object.
            try
            {
                // Only remove a file created by this call. If CreateNew failed because
                // a key already exists, the existing private object belongs to another
                // operation and must not be deleted.
                if (fileCreated && File.Exists(fullPath)) File.Delete(fullPath);
            }
            catch
            {
                // Preserve the original upload failure; cleanup is best effort.
            }
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(ResolvePrivatePath(storageKey), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Task.FromResult(stream);
    }

    public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePrivatePath(storageKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private string ResolvePrivatePath(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        var candidate = Path.GetFullPath(Path.Combine(_rootPath, storageKey.Replace('/', Path.DirectorySeparatorChar)));
        var rootWithSeparator = _rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Storage key is invalid.", nameof(storageKey));
        return candidate;
    }
}
