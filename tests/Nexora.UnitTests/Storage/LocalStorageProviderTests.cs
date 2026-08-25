using Microsoft.Extensions.Options;
using Nexora.Integrations.Storage;

namespace Nexora.UnitTests.Storage;

public sealed class LocalStorageProviderTests
{
    [Fact]
    public async Task SaveOpenDeleteUsesPrivateStorageKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new LocalStorageProvider(Options.Create(new LocalStorageOptions { RootPath = root }), TimeProvider.System);
            await using var input = new MemoryStream("private"u8.ToArray());
            var stored = await provider.SaveAsync(input, "cv.pdf", "application/pdf", CancellationToken.None);
            Assert.False(Path.IsPathRooted(stored.StorageKey));
            Assert.DoesNotContain(root, stored.StorageKey, StringComparison.OrdinalIgnoreCase);

            await using var output = await provider.OpenReadAsync(stored.StorageKey, CancellationToken.None);
            using var reader = new StreamReader(output);
            Assert.Equal("private", await reader.ReadToEndAsync(CancellationToken.None));
            await output.DisposeAsync();

            await provider.DeleteAsync(stored.StorageKey, CancellationToken.None);
            await Assert.ThrowsAsync<FileNotFoundException>(() => provider.OpenReadAsync(stored.StorageKey, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OpenReadRejectsPathTraversal()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new LocalStorageProvider(Options.Create(new LocalStorageOptions { RootPath = root }), TimeProvider.System);
            await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenReadAsync("../secret", CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
