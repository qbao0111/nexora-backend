using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Data.Persistence;
using Nexora.Integrations.Storage;
using UglyToad.PdfPig.Writer;

namespace Nexora.IntegrationTests;

public sealed class R2UploadApiTests
{
    private const string PdfContentType = "application/pdf";
    private const string R2Endpoint = "https://account-id.r2.cloudflarestorage.com";

    [Fact]
    public async Task PresignDirectObjectAndFinalizeCreateOneDurableResumeFlow()
    {
        var objectClient = new FakeR2ObjectClient(R2Endpoint);
        using var factory = CreateFactory(objectClient);
        factory.InitializeDatabase();
        Assert.Equal("r2", factory.Services.GetRequiredService<IOptions<StorageOptions>>().Value.Provider);
        using (var providerScope = factory.Services.CreateScope())
            Assert.IsType<R2UploadProvider>(providerScope.ServiceProvider.GetRequiredService<IUploadProvider>());
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var bytes = CreatePdf();

        var intent = await PresignAsync(client, "cv.pdf", PdfContentType, bytes.Length);
        Assert.StartsWith(R2Endpoint, intent.UploadUrl, StringComparison.Ordinal);
        Assert.True(intent.ExpiresAt > DateTimeOffset.UtcNow);
        objectClient.Seed(objectClient.LastPresignKey!, bytes);

        using var finalize = await client.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = intent.Token });
        Assert.Equal(HttpStatusCode.Created, finalize.StatusCode);
        var resume = await DataAsync(finalize);

        using var replay = await client.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = intent.Token });
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(resume.GetProperty("id").GetGuid(), (await DataAsync(replay)).GetProperty("id").GetGuid());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.UploadIntents.CountAsync(item => item.UserId == account.UserId && item.CompletedAt != null));
        Assert.Equal(1, await db.StoredFiles.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(1, await db.Resumes.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(1, await db.OutboxEvents.CountAsync(item => item.AggregateId == resume.GetProperty("id").GetGuid()));
        Assert.Equal(objectClient.LastPresignKey, objectClient.LastMetadataKey);
        Assert.Equal(objectClient.LastPresignKey, objectClient.LastReadKey);
    }

    [Fact]
    public async Task ConcurrentFinalizeRequestsConvergeOnOneResume()
    {
        var objectClient = new FakeR2ObjectClient(R2Endpoint);
        using var factory = CreateFactory(objectClient);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var bytes = CreatePdf();

        var intent = await PresignAsync(client, "cv.pdf", PdfContentType, bytes.Length);
        objectClient.Seed(objectClient.LastPresignKey!, bytes);

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = intent.Token }),
            client.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = intent.Token }));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var payloads = await Task.WhenAll(responses.Select(DataAsync));
            var resumeIds = payloads.Select(data => data.GetProperty("id").GetGuid()).Distinct().ToArray();
            Assert.Single(resumeIds);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(1, await db.UploadIntents.CountAsync(item => item.UserId == account.UserId && item.CompletedAt != null));
            Assert.Equal(1, await db.StoredFiles.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(1, await db.Resumes.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(1, await db.OutboxEvents.CountAsync(item => item.AggregateId == resumeIds[0]));
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task WrongOwnerCannotFinalizeAndCorruptObjectCreatesNoResume()
    {
        var objectClient = new FakeR2ObjectClient(R2Endpoint);
        using var factory = CreateFactory(objectClient);
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        var bytes = CreatePdf();
        var intent = await PresignAsync(ownerClient, "cv.pdf", PdfContentType, bytes.Length);
        objectClient.Seed(objectClient.LastPresignKey!, bytes);

        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        using var wrongOwner = await otherClient.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = intent.Token });
        Assert.Equal(HttpStatusCode.NotFound, wrongOwner.StatusCode);

        var corruptIntent = await PresignAsync(ownerClient, "corrupt.pdf", PdfContentType, 28);
        var corruptKey = objectClient.LastPresignKey!;
        objectClient.Seed(corruptKey, "%PDF-1.7\nnot a PDF container"u8.ToArray());
        using var corrupt = await ownerClient.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = corruptIntent.Token });
        Assert.Equal(HttpStatusCode.BadRequest, corrupt.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await db.StoredFiles.CountAsync(item => item.UserId == owner.UserId));
        Assert.Equal(0, await db.Resumes.CountAsync(item => item.UserId == owner.UserId));
        Assert.Equal(0, await db.UploadIntents.CountAsync(item => item.UserId == owner.UserId && item.CompletedAt != null));
        Assert.DoesNotContain(corruptKey, objectClient.LastDeletedKeys);
    }

    [Fact]
    public async Task R2RejectsExecutableMasqueradingAsPdfBeforeResumeCreation()
    {
        var objectClient = new FakeR2ObjectClient(R2Endpoint);
        using var factory = CreateFactory(objectClient);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var executable = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00 };

        var intent = await PresignAsync(client, "cv.pdf", PdfContentType, executable.Length);
        var storageKey = objectClient.LastPresignKey!;
        objectClient.Seed(storageKey, executable);

        using var response = await client.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = intent.Token });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await db.StoredFiles.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(0, await db.Resumes.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(0, await db.UploadIntents.CountAsync(item => item.UserId == account.UserId && item.CompletedAt != null));
    }

    [Fact]
    public async Task ExpiredIntentCannotFinalizeThroughApi()
    {
        var objectClient = new FakeR2ObjectClient(R2Endpoint);
        using var factory = CreateFactory(objectClient);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var bytes = CreatePdf();
        var intent = await PresignAsync(client, "expired.pdf", PdfContentType, bytes.Length);
        objectClient.Seed(objectClient.LastPresignKey!, bytes);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var record = await db.UploadIntents.SingleAsync(item => item.UserId == account.UserId);
            record.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var response = await client.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = intent.Token });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var scopeAfter = factory.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await dbAfter.StoredFiles.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(0, await dbAfter.Resumes.CountAsync(item => item.UserId == account.UserId));
    }

    private static NexoraApiFactory CreateFactory(FakeR2ObjectClient objectClient) =>
        new("Development", new Dictionary<string, string?>
        {
            ["Features:Upload"] = "true",
            ["Storage:Provider"] = "r2",
            ["Storage:R2:AccountId"] = "account-id",
            ["Storage:R2:Bucket"] = "private-test-bucket",
            ["Storage:R2:AccessKeyId"] = "test-access-key",
            ["Storage:R2:SecretAccessKey"] = "test-secret-key",
            ["Storage:R2:Endpoint"] = R2Endpoint
        }, services =>
        {
            services.RemoveAll<IR2ObjectClient>();
            services.AddSingleton<IR2ObjectClient>(objectClient);
        });

    private static async Task<UploadIntentResponse> PresignAsync(HttpClient client, string fileName, string contentType, int size)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/uploads/presign", new { fileName, contentType, size });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        return new UploadIntentResponse(
            data.GetProperty("token").GetString()!,
            data.GetProperty("uploadUrl").GetString()!,
            data.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private static byte[] CreatePdf()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(612, 792);
        return builder.Build();
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"r2-upload-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "R2 upload candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);
    private sealed record UploadIntentResponse(string Token, string UploadUrl, DateTimeOffset ExpiresAt);

    private sealed class FakeR2ObjectClient(string endpoint) : IR2ObjectClient
    {
        private readonly ConcurrentDictionary<string, byte[]> objects = new(StringComparer.Ordinal);

        public string? LastPresignKey { get; private set; }
        public string? LastMetadataKey { get; private set; }
        public string? LastReadKey { get; private set; }
        public List<string> LastDeletedKeys { get; } = [];

        public string CreatePresignedPutUrl(string bucket, string key, string contentType, DateTimeOffset expiresAt)
        {
            LastPresignKey = key;
            return $"{endpoint.TrimEnd('/')}/{bucket}/{key}?signature=test&expires={expiresAt.ToUnixTimeSeconds()}";
        }

        public Task<R2ObjectMetadata> GetMetadataAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastMetadataKey = key;
            if (!objects.TryGetValue(Composite(bucket, key), out var bytes))
                throw new FileNotFoundException("Synthetic object was not found.", key);
            return Task.FromResult(new R2ObjectMetadata(bytes.LongLength, "application/pdf"));
        }

        public Task<Stream> OpenReadAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastReadKey = key;
            if (!objects.TryGetValue(Composite(bucket, key), out var bytes))
                throw new FileNotFoundException("Synthetic object was not found.", key);
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }

        public async Task<long> PutObjectAsync(string bucket, string key, Stream content, string contentType, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            objects[Composite(bucket, key)] = buffer.ToArray();
            return buffer.Length;
        }

        public Task DeleteObjectAsync(string bucket, string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            objects.TryRemove(Composite(bucket, key), out _);
            LastDeletedKeys.Add(key);
            return Task.CompletedTask;
        }

        public void Seed(string key, byte[] bytes) => objects[Composite("private-test-bucket", key)] = bytes;

        private static string Composite(string bucket, string key) => $"{bucket}\0{key}";
    }
}
