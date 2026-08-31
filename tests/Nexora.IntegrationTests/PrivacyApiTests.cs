using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Business.Storage;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class PrivacyApiTests
{
    [Fact]
    public async Task ExportThenDeletionRemovesPersonalDataAndPrivateObjectT09()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var bytes = PdfTestDocument.CreateTextPdf("Private privacy resume");
        using var presign = await client.PostAsJsonAsync("/api/v1/uploads/presign", new
        {
            fileName = "private-resume.pdf",
            contentType = "application/pdf",
            size = bytes.Length
        });
        var intent = await DataAsync(presign);
        using (var upload = await client.PutAsync(intent.GetProperty("uploadUrl").GetString(), new ByteArrayContent(bytes)))
            Assert.Equal(HttpStatusCode.NoContent, upload.StatusCode);
        using (var finalize = await client.PostAsJsonAsync("/api/v1/resumes", new { uploadToken = intent.GetProperty("token").GetString() }))
            Assert.Equal(HttpStatusCode.Created, finalize.StatusCode);
        using (var jd = await client.PostAsJsonAsync("/api/v1/job-descriptions", new
        {
            title = "Private role",
            content = "Private candidate requirements"
        })) Assert.Equal(HttpStatusCode.Created, jd.StatusCode);

        string storageKey;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            storageKey = await db.StoredFiles.Where(item => item.UserId == account.UserId).Select(item => item.StorageKey).SingleAsync();
            await SeedPendingInterviewAsync(db, account.UserId);
        }

        using var export = await client.GetAsync("/api/v1/me/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var exported = await DataAsync(export);
        Assert.Equal(account.UserId, exported.GetProperty("profile").GetProperty("id").GetGuid());
        Assert.Equal("private-resume.pdf", exported.GetProperty("resumes")[0].GetProperty("fileName").GetString());
        Assert.Equal("Private role", exported.GetProperty("jobDescriptions")[0].GetProperty("title").GetString());
        Assert.DoesNotContain("storageKey", exported.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var deletionRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        deletionRequest.Headers.Add("Idempotency-Key", "privacy-delete-one");
        using var deletion = await client.SendAsync(deletionRequest);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);
        Assert.Equal(PrivacyValues.Queued, (await DataAsync(deletion)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);

        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>().ProcessPendingAsync(CancellationToken.None));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(0, await db.StoredFiles.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, await db.Resumes.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, await db.JobDescriptions.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, await db.UserProfiles.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, await db.RefreshTokens.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, (await db.Entitlements.SingleAsync(item => item.UserId == account.UserId)).Reserved);
            Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Void));
            var user = await db.Users.SingleAsync(item => item.Id == account.UserId);
            Assert.NotNull(user.DeletedAt);
            Assert.EndsWith("@invalid.local", user.Email, StringComparison.Ordinal);
            Assert.Equal(PrivacyValues.Completed, (await db.DataPrivacyRequests.SingleAsync()).Status);
            var storage = scope.ServiceProvider.GetRequiredService<IStorageProvider>();
            await Assert.ThrowsAsync<FileNotFoundException>(() => storage.OpenReadAsync(storageKey, CancellationToken.None));
        }
    }

    private static async Task SeedPendingInterviewAsync(NexoraDbContext db, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanPriceId = Guid.Parse("10000000-0000-0000-0000-000000000001"),
            PlanCodeSnapshot = "free",
            AmountMinor = 0,
            Currency = "VND",
            InterviewQuota = 1,
            Status = BillingValues.Fulfilled,
            PaymentProvider = "test",
            ProviderTransactionId = Guid.NewGuid().ToString("N"),
            CheckoutUrl = "https://example.test",
            CreatedAt = now,
            UpdatedAt = now
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrderId = order.Id,
            Status = BillingValues.Active,
            StartsAt = now.AddMinutes(-1),
            EndsAt = now.AddDays(1),
            CreatedAt = now,
            UpdatedAt = now
        };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SubscriptionId = subscription.Id,
            PlanCodeSnapshot = "free",
            Status = BillingValues.Active,
            InterviewLimit = 1,
            Reserved = 1,
            StartsAt = subscription.StartsAt,
            EndsAt = subscription.EndsAt,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        var reservation = new UsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementId = entitlement.Id,
            Action = BillingValues.Reserve,
            Quantity = 1,
            SourceType = "interview",
            SourceId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = now
        };
        var session = new InterviewSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ReservationEventId = reservation.Id,
            ReservationEvent = reservation,
            Role = "Private role",
            Seniority = "junior",
            InterviewType = "behavioral",
            Difficulty = "medium",
            Status = PracticeValues.Starting,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        reservation.SourceId = session.Id.ToString("N");
        db.AddRange(order, subscription, entitlement, reservation, session);
        await db.SaveChangesAsync();
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"privacy-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "Private candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await DataAsync(response);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
