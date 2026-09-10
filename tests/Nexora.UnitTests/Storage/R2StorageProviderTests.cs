using System.Text;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Storage;
using Nexora.Integrations.Storage;

namespace Nexora.UnitTests.Storage;

public sealed class R2StorageProviderTests
{
    private static readonly R2StorageOptions R2Options = new()
    {
        AccountId = "account-id",
        Bucket = "private-bucket",
        AccessKeyId = "access-key",
        SecretAccessKey = "secret-key",
        Endpoint = "https://account-id.r2.cloudflarestorage.com"
    };

    [Fact]
    public async Task SaveUsesConfiguredBucketAndReturnsStoredSize()
    {
        var client = new RecordingR2ObjectClient();
        var provider = CreateProvider(client);
        await using var content = new MemoryStream(Encoding.UTF8.GetBytes("private content"));

        var stored = await provider.SaveAsync(content, "cv.pdf", "application/pdf", CancellationToken.None);

        Assert.Equal(R2Options.Bucket, client.LastBucket);
        Assert.Equal("application/pdf", client.LastContentType);
        Assert.Equal("private content", Encoding.UTF8.GetString(client.LastContent!));
        Assert.Equal(client.LastContent!.LongLength, stored.Size);
        Assert.Equal("cv.pdf", stored.FileName);
        Assert.StartsWith("2026/09/", stored.StorageKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenReadReturnsReadableStreamAfterCallReturns()
    {
        var client = new RecordingR2ObjectClient { ReadContent = "private object"u8.ToArray() };
        var provider = CreateProvider(client);

        await using var output = await provider.OpenReadAsync("resumes/user-id/cv.pdf", CancellationToken.None);
        using var reader = new StreamReader(output, leaveOpen: true);

        Assert.Equal("private object", await reader.ReadToEndAsync(CancellationToken.None));
        Assert.Equal(R2Options.Bucket, client.LastBucket);
        Assert.Equal("resumes/user-id/cv.pdf", client.LastKey);
    }

    [Fact]
    public async Task DeleteTargetsConfiguredBucketAndExactKey()
    {
        var client = new RecordingR2ObjectClient();
        var provider = CreateProvider(client);

        await provider.DeleteAsync("avatars/user-id/avatar.png", CancellationToken.None);

        Assert.Equal(R2Options.Bucket, client.LastBucket);
        Assert.Equal("avatars/user-id/avatar.png", client.LastKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../secret")]
    [InlineData("resumes/../secret")]
    [InlineData("/absolute/path")]
    [InlineData("C:\\absolute\\path")]
    [InlineData("resumes\\user\\cv.pdf")]
    [InlineData("resumes//user/cv.pdf")]
    [InlineData("resumes/./user/cv.pdf")]
    [InlineData("resumes/\0cv.pdf")]
    public async Task OpenReadRejectsUnsafeLogicalKeys(string key)
    {
        var provider = CreateProvider(new RecordingR2ObjectClient());

        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenReadAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task CancellationIsPassedToClientAndNotMappedToProviderFailure()
    {
        var client = new RecordingR2ObjectClient();
        var provider = CreateProvider(client);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => provider.DeleteAsync(
            "resumes/user-id/cv.pdf", cancellation.Token));
        Assert.Equal(cancellation.Token, client.LastCancellationToken);
    }

    [Fact]
    public async Task ProviderErrorsBecomeSafeBusinessFailures()
    {
        var client = new RecordingR2ObjectClient { Failure = new InvalidOperationException("secret-key must not escape") };
        var provider = CreateProvider(client);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.DeleteAsync(
            "resumes/user-id/cv.pdf", CancellationToken.None));

        Assert.Equal("STORAGE_PROVIDER_UNAVAILABLE", exception.Code);
        Assert.DoesNotContain("secret-key", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PutRequestDoesNotAskForPublicAcl()
    {
        using var content = new MemoryStream();

        var request = R2ObjectClient.CreatePutObjectRequest(
            R2Options.Bucket, "resumes/user-id/cv.pdf", content, "application/pdf", useChunkEncoding: false);

        Assert.Equal(R2Options.Bucket, request.BucketName);
        Assert.Equal("resumes/user-id/cv.pdf", request.Key);
        Assert.Equal("application/pdf", request.ContentType);
        Assert.Null(request.CannedACL);
        Assert.False(request.AutoCloseStream);
    }

    [Fact]
    public async Task ResponseStreamOwnsResponseUntilCallerDisposesIt()
    {
        var owner = new TrackingDisposable();
        await using var output = new R2ResponseStream(new MemoryStream("still readable"u8.ToArray()), owner);

        using var reader = new StreamReader(output, leaveOpen: true);
        Assert.Equal("still readable", await reader.ReadToEndAsync(CancellationToken.None));
        Assert.False(owner.IsDisposed);

        await output.DisposeAsync();
        Assert.True(owner.IsDisposed);
    }

    private static R2StorageProvider CreateProvider(RecordingR2ObjectClient client) =>
        new(Microsoft.Extensions.Options.Options.Create(R2Options), client, new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<R2StorageProvider>.Instance);

    private sealed class RecordingR2ObjectClient : IR2ObjectClient
    {
        public string? LastBucket { get; private set; }
        public string? LastKey { get; private set; }
        public string? LastContentType { get; private set; }
        public byte[]? LastContent { get; private set; }
        public byte[] ReadContent { get; init; } = "content"u8.ToArray();
        public Exception? Failure { get; init; }
        public CancellationToken LastCancellationToken { get; private set; }

        public Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            Capture(bucket, key, cancellationToken);
            ThrowIfConfigured();
            return Task.FromResult<Stream>(new MemoryStream(ReadContent, writable: false));
        }

        public async Task<long> PutObjectAsync(
            string bucket,
            string key,
            Stream content,
            string contentType,
            CancellationToken cancellationToken)
        {
            Capture(bucket, key, cancellationToken);
            LastContentType = contentType;
            ThrowIfConfigured();
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            LastContent = buffer.ToArray();
            return LastContent.LongLength;
        }

        public Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            Capture(bucket, key, cancellationToken);
            ThrowIfConfigured();
            return Task.CompletedTask;
        }

        private void Capture(string bucket, string key, CancellationToken cancellationToken)
        {
            LastBucket = bucket;
            LastKey = key;
            LastCancellationToken = cancellationToken;
        }

        private void ThrowIfConfigured()
        {
            LastCancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool IsDisposed { get; private set; }
        public void Dispose() => IsDisposed = true;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
