using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Feedback;
using Nexora.Business.Privacy;
using Nexora.Business.Storage;
using Nexora.Data.Feedback;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class AvatarApiTests
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1];
    private static readonly byte[] Webp = "RIFF1234WEBPimage"u8.ToArray();

    [PostgresFact]
    public async Task AvatarMigrationAndReplacementWorkOnPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        await using var factory = NexoraApiFactory.CreatePostgres(connectionString);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client, "avatar-postgres@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var first = await UploadAsync(client, Jpeg, "image/jpeg");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstUrl = (await DataAsync(first)).GetProperty("avatarUrl").GetString()!;
        using var second = await UploadAsync(client, Png, "image/png");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondUrl = (await DataAsync(second)).GetProperty("avatarUrl").GetString()!;
        Assert.NotEqual(firstUrl, secondUrl);
        using (var stale = await client.GetAsync(firstUrl)) Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        using (var current = await client.GetAsync(secondUrl)) Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(Guid.Parse(secondUrl.Split('/').Last()), await db.UserProfiles
            .Where(item => item.UserId == owner.UserId).Select(item => item.AvatarId).SingleAsync());
    }

    [Theory]
    [InlineData("image/jpeg", "jpeg")]
    [InlineData("image/png", "png")]
    [InlineData("image/webp", "webp")]
    public async Task UploadAcceptsOnlyMatchingImageSignatures(string contentType, string kind)
    {
        var storage = new RecordingStorage();
        using var factory = CreateFactory(storage);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client, $"avatar-{kind}@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var bytes = kind switch { "jpeg" => Jpeg, "png" => Png, _ => Webp };

        using var upload = await UploadAsync(client, bytes, contentType);
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var avatarUrl = (await DataAsync(upload)).GetProperty("avatarUrl").GetString()!;
        using var current = await client.GetAsync("/api/v1/me");
        var currentJson = await DataAsync(current);
        Assert.Equal(avatarUrl, currentJson.GetProperty("avatarUrl").GetString());
        Assert.False(currentJson.GetProperty("billing").ValueKind == JsonValueKind.Undefined);
        Assert.DoesNotContain("storageKey", currentJson.GetRawText(), StringComparison.OrdinalIgnoreCase);
        using (var career = await client.GetAsync("/api/v1/me/career-profile"))
            Assert.Equal(avatarUrl, (await DataAsync(career)).GetProperty("profile").GetProperty("avatarUrl").GetString());

        using var anonymous = factory.CreateHttpsClient();
        using var image = await anonymous.GetAsync(avatarUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal(contentType, image.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("max-age=300", image.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Null(image.Content.Headers.ContentDisposition);
        Assert.Equal(bytes, await image.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task InvalidFilesAndAnonymousUploadAreRejectedWithoutStorageWrites()
    {
        var storage = new RecordingStorage();
        using var factory = CreateFactory(storage);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        using (var anonymous = await UploadAsync(client, Jpeg, "image/jpeg"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var owner = await RegisterAsync(client, "avatar-invalid@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);

        foreach (var (bytes, contentType, code) in new[]
        {
            (new byte[2 * 1024 * 1024 + 1], "image/png", "AVATAR_FILE_TOO_LARGE"),
            (Jpeg, "application/octet-stream", "AVATAR_FILE_TYPE_INVALID"),
            ("<svg/>"u8.ToArray(), "image/svg+xml", "AVATAR_FILE_TYPE_INVALID"),
            ("<html/>"u8.ToArray(), "image/png", "AVATAR_FILE_INVALID"),
            (Jpeg, "image/png", "AVATAR_FILE_INVALID")
        })
        {
            using var response = await UploadAsync(client, bytes, contentType);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(code, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        Assert.Empty(storage.Objects);
    }

    [Fact]
    public async Task ReplaceDeleteAndStorageFailureKeepOnlyCurrentCapabilityAccessible()
    {
        var storage = new RecordingStorage();
        using var factory = CreateFactory(storage);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client, "avatar-replace@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var first = await UploadAsync(client, Jpeg, "image/jpeg");
        var firstUrl = (await DataAsync(first)).GetProperty("avatarUrl").GetString()!;
        var firstKey = Assert.Single(storage.Objects.Keys);

        storage.FailNextSave = true;
        using (var failed = await UploadAsync(client, Png, "image/png"))
            Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using (var current = await client.GetAsync("/api/v1/me"))
            Assert.Equal(firstUrl, (await DataAsync(current)).GetProperty("avatarUrl").GetString());
        Assert.True(storage.Objects.ContainsKey(firstKey));

        storage.FailNextDelete = true;
        using var second = await UploadAsync(client, Png, "image/png");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondUrl = (await DataAsync(second)).GetProperty("avatarUrl").GetString()!;
        Assert.NotEqual(firstUrl, secondUrl);
        Assert.True(storage.Objects.ContainsKey(firstKey)); // Cleanup failed, but private object has no current capability.
        using (var stale = await client.GetAsync(firstUrl)) Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        using (var latest = await client.GetAsync(secondUrl)) Assert.Equal(HttpStatusCode.OK, latest.StatusCode);

        using (var remove = await client.DeleteAsync("/api/v1/me/avatar"))
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        using (var repeat = await client.DeleteAsync("/api/v1/me/avatar"))
            Assert.Equal(HttpStatusCode.NoContent, repeat.StatusCode);
        using (var stale = await client.GetAsync(secondUrl)) Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        using (var current = await client.GetAsync("/api/v1/me"))
            Assert.Equal(JsonValueKind.Null, (await DataAsync(current)).GetProperty("avatarUrl").ValueKind);
    }

    [Fact]
    public async Task AnotherUserCanOnlyChangeTheirOwnAvatar()
    {
        var storage = new RecordingStorage();
        using var factory = CreateFactory(storage);
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "avatar-owner@example.test");
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var ownerUpload = await UploadAsync(ownerClient, Jpeg, "image/jpeg");
        var ownerUrl = (await DataAsync(ownerUpload)).GetProperty("avatarUrl").GetString();

        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient, "avatar-other@example.test");
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.Token);
        using (var otherUpload = await UploadAsync(otherClient, Png, "image/png"))
            Assert.NotEqual(ownerUrl, (await DataAsync(otherUpload)).GetProperty("avatarUrl").GetString());
        using (var otherDelete = await otherClient.DeleteAsync("/api/v1/me/avatar"))
            Assert.Equal(HttpStatusCode.NoContent, otherDelete.StatusCode);

        using var stillOwner = await ownerClient.GetAsync("/api/v1/me");
        Assert.Equal(ownerUrl, (await DataAsync(stillOwner)).GetProperty("avatarUrl").GetString());
        using var stillReadable = await otherClient.GetAsync(ownerUrl);
        Assert.Equal(HttpStatusCode.OK, stillReadable.StatusCode);
    }

    [Fact]
    public async Task PublicFeedbackAvatarFollowsCurrentAvatarWithoutChangingModeration()
    {
        var storage = new RecordingStorage();
        using var factory = CreateFactory(storage);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client, "avatar-feedback@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.ProductFeedbacks.Add(new ProductFeedback
            {
                Id = Guid.NewGuid(), UserId = owner.UserId, Rating = 5, Comment = "Public comment",
                Consent = true, Status = FeedbackValues.Approved, PublishedAt = now,
                CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }
        using var guest = factory.CreateHttpsClient();
        using (var initial = await guest.GetAsync("/api/v1/feedback/public"))
            Assert.Equal(JsonValueKind.Null, (await DataAsync(initial)).GetProperty("items")[0].GetProperty("avatarUrl").ValueKind);

        using var upload = await UploadAsync(client, Webp, "image/webp");
        var avatarUrl = (await DataAsync(upload)).GetProperty("avatarUrl").GetString();
        using (var published = await guest.GetAsync("/api/v1/feedback/public"))
        {
            var item = (await DataAsync(published)).GetProperty("items")[0];
            Assert.Equal(avatarUrl, item.GetProperty("avatarUrl").GetString());
            Assert.DoesNotContain("email", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("storageKey", item.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        using (var remove = await client.DeleteAsync("/api/v1/me/avatar"))
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        using (var published = await guest.GetAsync("/api/v1/feedback/public"))
        {
            var item = (await DataAsync(published)).GetProperty("items")[0];
            Assert.Equal(JsonValueKind.Null, item.GetProperty("avatarUrl").ValueKind);
        }
        using var scopeAfter = factory.Services.CreateScope();
        var dbAfter = scopeAfter.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(FeedbackValues.Approved, await dbAfter.ProductFeedbacks.Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task AccountDeletionRemovesPrivateAvatarAndPublicCapability()
    {
        var storage = new RecordingStorage();
        using var factory = CreateFactory(storage);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client, "avatar-delete-account@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var upload = await UploadAsync(client, Jpeg, "image/jpeg");
        var avatarUrl = (await DataAsync(upload)).GetProperty("avatarUrl").GetString()!;
        var key = Assert.Single(storage.Objects.Keys);

        using (var export = await client.GetAsync("/api/v1/me/export"))
            Assert.DoesNotContain("storageKey", (await DataAsync(export)).GetRawText(), StringComparison.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        request.Headers.Add("Idempotency-Key", "avatar-account-deletion");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using (var hidden = await client.GetAsync(avatarUrl)) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>().ProcessPendingAsync(CancellationToken.None));
        Assert.False(storage.Objects.ContainsKey(key));
        using var gone = await client.GetAsync(avatarUrl);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    private static NexoraApiFactory CreateFactory(RecordingStorage storage) =>
        new(new Dictionary<string, string?>(), services =>
        {
            services.RemoveAll<IStorageProvider>();
            services.AddSingleton<IStorageProvider>(storage);
        });

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, byte[] bytes, string contentType)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "avatar.png");
        return await client.PutAsync("/api/v1/me/avatar", form);
    }

    private static async Task<(Guid UserId, string Token)> RegisterAsync(HttpClient client, string email)
    {
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, password = "Strong!Pass123", displayName = "Avatar owner"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return (data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed class RecordingStorage : IStorageProvider
    {
        public ConcurrentDictionary<string, byte[]> Objects { get; } = new();
        public bool FailNextSave { get; set; }
        public bool FailNextDelete { get; set; }

        public async Task<StoredObject> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken)
        {
            if (FailNextSave) { FailNextSave = false; throw new IOException("Synthetic storage failure"); }
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            var key = $"private/avatars/{Guid.NewGuid():N}";
            Objects[key] = buffer.ToArray();
            return new StoredObject(key, fileName, contentType, buffer.Length);
        }

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(Objects.TryGetValue(storageKey, out var bytes)
                ? new MemoryStream(bytes) : throw new FileNotFoundException());

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
        {
            if (FailNextDelete) { FailNextDelete = false; throw new IOException("Synthetic deletion failure"); }
            Objects.TryRemove(storageKey, out _);
            return Task.CompletedTask;
        }
    }
}
