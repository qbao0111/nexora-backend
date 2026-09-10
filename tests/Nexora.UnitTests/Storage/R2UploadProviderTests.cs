using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Integrations.Storage;
using UglyToad.PdfPig.Writer;

namespace Nexora.UnitTests.Storage;

public sealed class R2UploadProviderTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly R2StorageOptions R2Options = new()
    {
        AccountId = "account-id",
        Bucket = "private-bucket",
        AccessKeyId = "access-key",
        SecretAccessKey = "secret-key",
        Endpoint = "https://account-id.r2.cloudflarestorage.com"
    };

    [Fact]
    public async Task CreateIntentUsesIndependentRandomTokenAndPrivateUserScopedKey()
    {
        var store = new FakeIntentStore();
        var client = new FakeR2ObjectClient();
        var provider = CreateProvider(store, client, new FakeStorageProvider());
        var bytes = ValidPdf();

        var first = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);
        var second = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(64, first.Token.Length);
        Assert.NotEqual(first.Token, store.State!.Id.ToString("N"));
        Assert.StartsWith("resumes/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", store.State!.StorageKey, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Token, store.TokenHash, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, store.CreateCalls);
        Assert.Equal(2, client.PresignKeys.Count);
    }

    [Fact]
    public async Task FinalizeValidatesActualObjectAndReplayReturnsSameMetadata()
    {
        var bytes = ValidPdf();
        var store = new FakeIntentStore();
        var client = new FakeR2ObjectClient { MetadataSize = bytes.LongLength };
        var storage = new FakeStorageProvider(bytes);
        var provider = CreateProvider(store, client, storage);
        var intent = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);

        var completed = await provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None);
        var replay = await provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None);

        Assert.Equal(bytes.LongLength, completed.Size);
        Assert.Equal(completed.Checksum, replay.Checksum);
        Assert.Equal(1, store.CompleteCalls);
        Assert.Equal(1, storage.OpenCalls);
    }

    [Fact]
    public async Task CompletedIntentCannotIssueAnotherUploadUrl()
    {
        var bytes = ValidPdf();
        var store = new FakeIntentStore();
        var client = new FakeR2ObjectClient { MetadataSize = bytes.LongLength };
        var provider = CreateProvider(store, client, new FakeStorageProvider(bytes));
        var intent = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);
        await provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None);

        var nextIntent = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);

        Assert.NotEqual(intent.Token, nextIntent.Token);
        Assert.Equal(2, client.PresignKeys.Count);
    }

    [Fact]
    public async Task FinalizeRejectsObjectSizeMismatchBeforeReadingContent()
    {
        var bytes = ValidPdf();
        var store = new FakeIntentStore();
        var client = new FakeR2ObjectClient { MetadataSize = bytes.LongLength + 1 };
        var storage = new FakeStorageProvider(bytes);
        var provider = CreateProvider(store, client, storage);
        var intent = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None));

        Assert.Equal("UPLOAD_SIZE_MISMATCH", exception.Code);
        Assert.Equal(0, storage.OpenCalls);
        Assert.Equal(0, store.CompleteCalls);
    }

    [Fact]
    public async Task ExpiredIntentCannotBeFinalized()
    {
        var bytes = ValidPdf();
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var client = new FakeR2ObjectClient { MetadataSize = bytes.LongLength };
        var provider = CreateProvider(new FakeIntentStore(), client, new FakeStorageProvider(bytes), clock);
        var intent = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(11));

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None));

        Assert.Equal("UPLOAD_INTENT_INVALID", exception.Code);
        Assert.Equal(0, client.MetadataCalls);
    }

    [Fact]
    public async Task WrongOwnerCannotFinalizeAnotherUsersIntent()
    {
        var bytes = ValidPdf();
        var store = new FakeIntentStore();
        var provider = CreateProvider(store, new FakeR2ObjectClient { MetadataSize = bytes.LongLength }, new FakeStorageProvider(bytes));
        var intent = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.GetCompletedAsync(Guid.NewGuid(), intent.Token, CancellationToken.None));

        Assert.Equal("UPLOAD_NOT_FOUND", exception.Code);
    }

    [Fact]
    public async Task CorruptObjectCannotBeFinalized()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-1.7\nnot a PDF container");
        var store = new FakeIntentStore();
        var client = new FakeR2ObjectClient { MetadataSize = bytes.LongLength };
        var provider = CreateProvider(store, client, new FakeStorageProvider(bytes));
        var intent = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", bytes.Length, CancellationToken.None);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.GetCompletedAsync(UserId, intent.Token, CancellationToken.None));

        Assert.Equal("UPLOAD_CONTAINER_INVALID", exception.Code);
        Assert.Equal(0, store.CompleteCalls);
    }

    [Fact]
    public async Task R2DirectUploadEndpointIsRejected()
    {
        var provider = CreateProvider(new FakeIntentStore(), new FakeR2ObjectClient(), new FakeStorageProvider());

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.UploadAsync(
            "token", new MemoryStream(), CancellationToken.None));

        Assert.Equal("UPLOAD_DIRECT_UPLOAD_UNSUPPORTED", exception.Code);
    }

    [Fact]
    public async Task EachIntentUsesASeparateCapability()
    {
        var store = new FakeIntentStore();
        var provider = CreateProvider(store, new FakeR2ObjectClient(), new FakeStorageProvider());
        var first = await provider.CreateIntentAsync(UserId, "cv.pdf", "application/pdf", 1, CancellationToken.None);
        var second = await provider.CreateIntentAsync(UserId, "other.pdf", "application/pdf", 1, CancellationToken.None);

        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(2, store.CreateCalls);
    }

    private static R2UploadProvider CreateProvider(
        FakeIntentStore store,
        FakeR2ObjectClient client,
        FakeStorageProvider storage,
        TimeProvider? clock = null) =>
        new(
            Options.Create(R2Options),
            Options.Create(new UploadOptions { MaxResumeBytes = 10 * 1024 * 1024, IntentMinutes = 10 }),
            store,
            client,
            storage,
            new UploadDocumentValidator(Options.Create(new UploadOptions { MaxResumeBytes = 10 * 1024 * 1024 })),
            clock ?? new FixedTimeProvider(new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<R2UploadProvider>.Instance);

    private static byte[] ValidPdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(612, 792);
        return builder.Build();
    }

    private sealed class FakeIntentStore : IUploadIntentStore
    {
        public UploadIntentState? State { get; private set; }
        public string TokenHash { get; private set; } = string.Empty;
        public int CreateCalls { get; private set; }
        public int CompleteCalls { get; private set; }

        public Task<UploadIntentState?> FindByTokenHashAsync(Guid userId, string tokenHash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(State is not null && State.UserId == userId && TokenHash == tokenHash ? State : null);
        }

        public Task<UploadIntentState> CreateAsync(UploadIntentState state, string tokenHash, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCalls++;
            State = state;
            TokenHash = tokenHash;
            return Task.FromResult(State);
        }

        public Task<UploadIntentState?> CompleteAsync(Guid userId, string tokenHash, long actualSize, string checksum, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State is null || State.UserId != userId || TokenHash != tokenHash) return Task.FromResult<UploadIntentState?>(null);
            if (State.IsCompleted) return Task.FromResult<UploadIntentState?>(State);
            CompleteCalls++;
            State = State with { ActualSize = actualSize, Checksum = checksum, CompletedAt = DateTimeOffset.UtcNow, Version = State.Version + 1 };
            return Task.FromResult<UploadIntentState?>(State);
        }
    }

    private sealed class FakeR2ObjectClient : IR2ObjectClient
    {
        public long MetadataSize { get; init; }
        public List<string> PresignKeys { get; } = [];
        public int MetadataCalls { get; private set; }

        public string CreatePresignedPutUrl(string bucket, string key, string contentType, DateTimeOffset expiresAt)
        {
            PresignKeys.Add(key);
            return $"https://signed.example/{key}";
        }

        public Task<R2ObjectMetadata> GetMetadataAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MetadataCalls++;
            return Task.FromResult(new R2ObjectMetadata(MetadataSize, "application/pdf"));
        }

        public Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task<long> PutObjectAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(0L);

        public Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeStorageProvider(byte[]? content = null) : IStorageProvider
    {
        private readonly byte[] content = content ?? [];
        public int OpenCalls { get; private set; }

        public Task<StoredObject> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredObject("unused", fileName, contentType, 0));

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCalls++;
            return Task.FromResult<Stream>(new MemoryStream(content, writable: false));
        }

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset current = now;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan amount) => current = current.Add(amount);
    }
}
