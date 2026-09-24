using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Authorization;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class SiteContentAndOrderHistoryApiTests
{
    [Fact]
    public async Task SiteDraftIsPrivateAndPublicationIsAudited()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var ordinary = await RegisterAsync(client);

        using var publicSettings = await client.GetAsync("/api/v1/public/site-settings");
        Assert.Equal(HttpStatusCode.OK, publicSettings.StatusCode);
        using var settingsJson = JsonDocument.Parse(await publicSettings.Content.ReadAsStringAsync());
        Assert.Equal("nexorainterview@gmail.com", settingsJson.RootElement.GetProperty("data").GetProperty("contactEmail").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/public/pages/terms")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/public/pages/unknown")).StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ordinary.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/v1/admin/site-pages/terms", new
        {
            title = "Điều khoản",
            bodyMarkdown = "Nội dung bản nháp.",
            about = (object?)null
        })).StatusCode);

        var admin = await PromoteAndLoginAsync(factory, client, ordinary);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/admin/site-settings", new
        {
            contactEmail = "not-an-email",
            brandDescription = "Nexora",
            facebookUrl = "javascript:alert(1)",
            tiktokUrl = (string?)null,
            supportAvailabilityEnabled = false,
            supportLabel = (string?)null,
            madeInVietnamEnabled = true
        })).StatusCode);
        using var updated = await client.PutAsJsonAsync("/api/v1/admin/site-pages/terms", new
        {
            title = "Điều khoản",
            bodyMarkdown = "Nội dung bản nháp.",
            about = (object?)null
        });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/public/pages/terms")).StatusCode);
        using var published = await client.PostAsync("/api/v1/admin/site-pages/terms/publish", null);
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        using var publicPage = await client.GetAsync("/api/v1/public/pages/terms");
        Assert.Equal(HttpStatusCode.OK, publicPage.StatusCode);
        using var pageJson = JsonDocument.Parse(await publicPage.Content.ReadAsStringAsync());
        Assert.Equal("Nội dung bản nháp.", pageJson.RootElement.GetProperty("data").GetProperty("bodyMarkdown").GetString());
        Assert.False(pageJson.RootElement.GetProperty("data").TryGetProperty("storageKey", out _));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(2, await db.AdminAuditEvents.CountAsync(item => item.Action.StartsWith("site.page.terms.")));
    }

    [Fact]
    public async Task OrdersArchiveIsOwnerScopedAndCursorPaginated()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        var other = await RegisterAsync(client);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var price = await db.PlanPrices.FirstAsync();
            var timestamp = DateTimeOffset.UtcNow;
            for (var index = 0; index < 26; index++)
                db.Orders.Add(new Order
                {
                    Id = Guid.NewGuid(),
                    UserId = owner.Id,
                    PlanPriceId = price.Id,
                    PlanCodeSnapshot = "basic",
                    AmountMinor = 49_000,
                    Currency = "VND",
                    Status = index % 2 == 0 ? "fulfilled" : "failed",
                    PaymentProvider = "fake",
                    ProviderTransactionId = $"archive-{Guid.NewGuid():N}",
                    CreatedAt = timestamp.AddMinutes(-(index / 2)),
                    UpdatedAt = timestamp
                });
            db.Orders.Add(new Order
            {
                Id = Guid.NewGuid(),
                UserId = other.Id,
                PlanPriceId = price.Id,
                PlanCodeSnapshot = "private",
                AmountMinor = 1,
                Currency = "VND",
                Status = "fulfilled",
                PaymentProvider = "fake",
                ProviderTransactionId = $"archive-{Guid.NewGuid():N}",
                CreatedAt = timestamp,
                UpdatedAt = timestamp
            });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var first = await client.GetAsync("/api/v1/me/orders?pageSize=20");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstData = firstJson.RootElement.GetProperty("data");
        Assert.Equal(20, firstData.GetProperty("items").GetArrayLength());
        var cursor = firstData.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        using var second = await client.GetAsync($"/api/v1/me/orders?pageSize=20&cursor={Uri.EscapeDataString(cursor!)}");
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Equal(6, secondJson.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, secondJson.RootElement.GetProperty("data").GetProperty("nextCursor").ValueKind);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/me/orders?pageSize=51")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/v1/me/orders?cursor=invalid")).StatusCode);
        using var filtered = await client.GetAsync("/api/v1/me/orders?status=failed&pageSize=20");
        using var filteredJson = JsonDocument.Parse(await filtered.Content.ReadAsStringAsync());
        Assert.Equal(13, filteredJson.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());
        using var me = await client.GetAsync("/api/v1/me");
        using var meJson = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(20, meJson.RootElement.GetProperty("data").GetProperty("billing").GetProperty("orders").GetArrayLength());
    }

    [PostgresFact]
    public async Task OrdersArchiveCursorTranslatesAndKeepsStableOrderOnPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        await using var factory = NexoraApiFactory.CreatePostgres(connectionString);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        var other = await RegisterAsync(client);
        var createdAt = new DateTimeOffset(2026, 9, 24, 12, 0, 0, TimeSpan.Zero);
        var lowerId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("10000000-0000-0000-0000-000000000002");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var price = await db.PlanPrices.FirstAsync();
            foreach (var (id, userId) in new[] { (lowerId, owner.Id), (higherId, owner.Id), (Guid.NewGuid(), other.Id) })
                db.Orders.Add(new Order
                {
                    Id = id,
                    UserId = userId,
                    PlanPriceId = price.Id,
                    PlanCodeSnapshot = "basic",
                    AmountMinor = 49_000,
                    Currency = "VND",
                    Status = "fulfilled",
                    PaymentProvider = "fake",
                    ProviderTransactionId = $"archive-{id:N}",
                    CreatedAt = createdAt,
                    UpdatedAt = createdAt
                });
            await db.SaveChangesAsync();
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var first = await client.GetAsync("/api/v1/me/orders?pageSize=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var firstData = firstJson.RootElement.GetProperty("data");
        Assert.Equal(higherId, firstData.GetProperty("items")[0].GetProperty("id").GetGuid());
        var cursor = firstData.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        using var second = await client.GetAsync($"/api/v1/me/orders?pageSize=1&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        using var secondJson = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        var secondData = secondJson.RootElement.GetProperty("data");
        Assert.Equal(lowerId, secondData.GetProperty("items")[0].GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, secondData.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task SiteAssetsRequireAdminAndValidateMimeMagicAndSize()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(client, "image/png", ValidPng)).StatusCode);

        var adminToken = await PromoteAndLoginAsync(factory, client, account);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, "image/svg+xml", "<svg/>"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, "image/png", "not a png"u8.ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(client, "image/png", new byte[5 * 1024 * 1024 + 1])).StatusCode);

        using var uploaded = await UploadAsync(client, "image/png", ValidPng);
        Assert.Equal(HttpStatusCode.Created, uploaded.StatusCode);
        using var json = JsonDocument.Parse(await uploaded.Content.ReadAsStringAsync());
        var asset = json.RootElement.GetProperty("data");
        Assert.False(asset.TryGetProperty("storageKey", out _));
        var assetId = asset.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/public/site-assets/{assetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/v1/admin/site-assets/{assetId}")).StatusCode);
        using var draft = await client.PutAsJsonAsync("/api/v1/admin/site-pages/about", new
        {
            title = "Giới thiệu",
            bodyMarkdown = (string?)null,
            about = new
            {
                heroTitle = "Nexora",
                heroSubtitle = "Luyện phỏng vấn có định hướng.",
                heroAssetId = assetId,
                missionTitle = "Sứ mệnh",
                missionBody = "Luyện tập có căn cứ.",
                missionAssetId = (Guid?)null,
                values = Array.Empty<object>(),
                milestones = Array.Empty<object>(),
                teamSectionEnabled = false,
                teamHeading = (string?)null,
                teamMembers = Array.Empty<object>()
            }
        });
        Assert.Equal(HttpStatusCode.OK, draft.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/v1/public/site-assets/{assetId}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/admin/site-pages/about/publish", null)).StatusCode);
        using var publicRead = await client.GetAsync($"/api/v1/public/site-assets/{assetId}");
        Assert.Equal(HttpStatusCode.OK, publicRead.StatusCode);
        Assert.Equal("image/png", publicRead.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", publicRead.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(ValidPng, await publicRead.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SettingsAndAboutRequireValidatedAdminWritesAndPublishedSnapshotStaysStable()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var adminToken = await PromoteAndLoginAsync(factory, client, account);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var settings = await client.PutAsJsonAsync("/api/v1/admin/site-settings", new
        {
            contactEmail = "hello@nexora.test",
            brandDescription = "Luyện tập có chủ đích.",
            facebookUrl = "https://facebook.com/nexora",
            tiktokUrl = "",
            supportAvailabilityEnabled = false,
            supportLabel = "24/7",
            madeInVietnamEnabled = true
        });
        Assert.Equal(HttpStatusCode.OK, settings.StatusCode);
        using var settingsJson = JsonDocument.Parse(await settings.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Null, settingsJson.RootElement.GetProperty("data").GetProperty("tiktokUrl").ValueKind);
        Assert.Equal(JsonValueKind.Null, settingsJson.RootElement.GetProperty("data").GetProperty("supportLabel").ValueKind);

        using var aboutDraft = await client.PutAsJsonAsync("/api/v1/admin/site-pages/about", new
        {
            title = "Giới thiệu Nexora",
            bodyMarkdown = (string?)null,
            about = new
            {
                heroTitle = "Luyện phỏng vấn có định hướng",
                heroSubtitle = "Phản hồi bám sát câu trả lời.",
                heroAssetId = (Guid?)null,
                missionTitle = "Sứ mệnh",
                missionBody = "Giúp bạn luyện tập và nhìn rõ tiến bộ.",
                missionAssetId = (Guid?)null,
                values = new[] { new { title = "Phản hồi có căn cứ", description = "Dựa trên câu trả lời.", iconKey = "target" } },
                milestones = Array.Empty<object>(),
                teamSectionEnabled = false,
                teamHeading = (string?)null,
                teamMembers = Array.Empty<object>()
            }
        });
        Assert.Equal(HttpStatusCode.OK, aboutDraft.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/public/pages/about")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/v1/admin/site-pages/about/publish", null)).StatusCode);
        using var updatedDraft = await client.PutAsJsonAsync("/api/v1/admin/site-pages/about", new
        {
            title = "Bản nháp mới",
            bodyMarkdown = (string?)null,
            about = new
            {
                heroTitle = "Bản nháp mới",
                heroSubtitle = "Chưa công bố.",
                heroAssetId = (Guid?)null,
                missionTitle = "Sứ mệnh",
                missionBody = "Dữ liệu nháp.",
                missionAssetId = (Guid?)null,
                values = Array.Empty<object>(),
                milestones = Array.Empty<object>(),
                teamSectionEnabled = false,
                teamHeading = (string?)null,
                teamMembers = Array.Empty<object>()
            },
            concurrencyToken = await GetPageTokenAsync(client, "about")
        });
        Assert.Equal(HttpStatusCode.OK, updatedDraft.StatusCode);
        using var publicRead = await client.GetAsync("/api/v1/public/pages/about");
        using var publicJson = JsonDocument.Parse(await publicRead.Content.ReadAsStringAsync());
        Assert.Equal("Giới thiệu Nexora", publicJson.RootElement.GetProperty("data").GetProperty("title").GetString());
        Assert.Equal("Luyện phỏng vấn có định hướng", publicJson.RootElement.GetProperty("data").GetProperty("about").GetProperty("heroTitle").GetString());
        Assert.Equal(JsonValueKind.Null, publicJson.RootElement.GetProperty("data").GetProperty("concurrencyToken").ValueKind);
    }

    private static readonly byte[] ValidPng = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 0];

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string contentType, byte[] bytes)
    {
        using var form = new MultipartFormDataContent();
        using var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "site.png");
        return await client.PostAsync("/api/v1/admin/site-assets", form);
    }

    private static async Task<Guid> GetPageTokenAsync(HttpClient client, string key)
    {
        using var response = await client.GetAsync($"/api/v1/admin/site-pages/{key}");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("concurrencyToken").GetGuid();
    }

    private static async Task<(Guid Id, string Token, string Email)> RegisterAsync(HttpClient client)
    {
        var email = $"site-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123", displayName = "Site Test" });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return (data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!, email);
    }

    private static async Task<string> PromoteAndLoginAsync(NexoraApiFactory factory, HttpClient client, (Guid Id, string Token, string Email) account)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (!await roles.RoleExistsAsync(RoleNames.Admin)) await roles.CreateAsync(new IdentityRole<Guid>(RoleNames.Admin));
            var user = await users.FindByIdAsync(account.Id.ToString());
            await users.AddToRoleAsync(user!, RoleNames.Admin);
        }
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = account.Email, password = "Strong!Pass123" });
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }
}
