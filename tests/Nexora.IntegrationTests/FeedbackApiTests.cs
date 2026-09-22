using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Authorization;
using Nexora.Business.Feedback;
using Nexora.Data.Feedback;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class FeedbackApiTests
{
    [Fact]
    public async Task UserFeedbackRequiresConsentAndModerationBeforePublicDisplay()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var user = await RegisterAsync(client, "feedback-user@example.test", "Candidate");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);

        using var put = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 5,
            comment = "Trải nghiệm luyện phỏng vấn rất hữu ích.",
            allowPublicDisplay = true
        });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var feedback = (await DataAsync(put)).GetProperty("moderationStatus").GetString();
        Assert.Equal("pending", feedback);

        using var anonymous = factory.CreateHttpsClient();
        using var hidden = await anonymous.GetAsync("/api/v1/feedback/public");
        Assert.Equal(HttpStatusCode.OK, hidden.StatusCode);
        var hiddenData = await DataAsync(hidden);
        Assert.Equal(0, hiddenData.GetProperty("ratingCount").GetInt32());
        Assert.Empty(hiddenData.GetProperty("items").EnumerateArray());

        var adminToken = await MakeAdminAsync(factory, user.UserId, "feedback-user@example.test");
        anonymous.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var list = await anonymous.GetAsync("/api/v1/admin/feedback?status=pending");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var feedbackId = (await DataAsync(list)).GetProperty("items")[0].GetProperty("id").GetGuid();

        using var approve = await anonymous.PostAsync($"/api/v1/admin/feedback/{feedbackId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using var featured = await anonymous.PostAsync($"/api/v1/admin/feedback/{feedbackId}/feature", null);
        Assert.Equal(HttpStatusCode.OK, featured.StatusCode);

        using var publicResponse = await anonymous.GetAsync("/api/v1/feedback/public?limit=20");
        var publicData = await DataAsync(publicResponse);
        Assert.Equal(1, publicData.GetProperty("ratingCount").GetInt32());
        Assert.Equal(5, publicData.GetProperty("averageRating").GetDouble());
        var publicItem = publicData.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(feedbackId, publicItem.GetProperty("id").GetGuid());
        Assert.DoesNotContain("userId", publicItem.EnumerateObject().Select(item => item.Name), StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", publicItem.EnumerateObject().Select(item => item.Name), StringComparer.OrdinalIgnoreCase);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        using var withdraw = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 4,
            comment = "Đã đổi ý.",
            allowPublicDisplay = false
        });
        Assert.Equal(HttpStatusCode.OK, withdraw.StatusCode);
        using var hiddenAgain = await anonymous.GetAsync("/api/v1/feedback/public");
        var hiddenAgainData = await DataAsync(hiddenAgain);
        Assert.Equal(0, hiddenAgainData.GetProperty("ratingCount").GetInt32());
        Assert.Empty(hiddenAgainData.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task FeedbackValidationAndAdminAuthorizationAreEnforced()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var user = await RegisterAsync(client, "feedback-validation@example.test", "Candidate");

        using var anonymous = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 0,
            comment = "invalid",
            allowPublicDisplay = false
        });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var anonymousAdmin = await client.GetAsync("/api/v1/admin/feedback");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousAdmin.StatusCode);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        using var invalid = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 6,
            comment = "invalid",
            allowPublicDisplay = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        using var nonAdmin = await client.GetAsync("/api/v1/admin/feedback");
        Assert.Equal(HttpStatusCode.Forbidden, nonAdmin.StatusCode);
    }

    [Fact]
    public async Task FeedbackUpsertModerationResetAndDeletePreserveOneCurrentRecord()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var user = await RegisterAsync(client, "feedback-lifecycle@example.test", "Candidate");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);

        using var invalidRating = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 0,
            comment = "invalid",
            allowPublicDisplay = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidRating.StatusCode);
        using var longComment = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 4,
            comment = new string('x', 1_001),
            allowPublicDisplay = false
        });
        Assert.Equal(HttpStatusCode.BadRequest, longComment.StatusCode);

        using var create = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 4,
            comment = "Bản đầu tiên",
            allowPublicDisplay = true
        });
        var feedbackId = (await DataAsync(create)).GetProperty("id").GetGuid();
        using var update = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 5,
            comment = "Bản cập nhật",
            allowPublicDisplay = true
        });
        Assert.Equal(feedbackId, (await DataAsync(update)).GetProperty("id").GetGuid());

        var adminToken = await MakeAdminAsync(factory, user.UserId, "feedback-lifecycle@example.test");
        using var admin = factory.CreateHttpsClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var approve = await admin.PostAsync($"/api/v1/admin/feedback/{feedbackId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approve.StatusCode);
        using var feature = await admin.PostAsync($"/api/v1/admin/feedback/{feedbackId}/feature", null);
        Assert.Equal(HttpStatusCode.OK, feature.StatusCode);

        using var edit = await client.PutAsJsonAsync("/api/v1/me/feedback", new
        {
            rating = 3,
            comment = "Cần duyệt lại",
            allowPublicDisplay = true
        });
        var edited = await DataAsync(edit);
        Assert.Equal("pending", edited.GetProperty("moderationStatus").GetString());
        Assert.False(edited.GetProperty("isFeatured").GetBoolean());
        using var cannotFeaturePending = await admin.PostAsync($"/api/v1/admin/feedback/{feedbackId}/feature", null);
        Assert.Equal(HttpStatusCode.Conflict, cannotFeaturePending.StatusCode);
        using var reject = await admin.PostAsync($"/api/v1/admin/feedback/{feedbackId}/reject", null);
        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Equal("rejected", (await DataAsync(reject)).GetProperty("moderationStatus").GetString());

        using var delete = await client.DeleteAsync("/api/v1/me/feedback");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var current = await client.GetAsync("/api/v1/me/feedback");
        Assert.Equal(JsonValueKind.Null, (await DataAsync(current)).ValueKind);
        using var publicResponse = await admin.GetAsync("/api/v1/feedback/public");
        var publicData = await DataAsync(publicResponse);
        Assert.Equal(0, publicData.GetProperty("ratingCount").GetInt32());
        Assert.Empty(publicData.GetProperty("items").EnumerateArray());

        using var summaryResponse = await admin.GetAsync("/api/v1/admin/feedback/summary");
        var summary = await DataAsync(summaryResponse);
        Assert.Equal(0, summary.GetProperty("total").GetInt32());
        var distribution = summary.GetProperty("ratingDistribution");
        Assert.All(Enumerable.Range(1, 5), rating => Assert.Equal(
            0,
            distribution.GetProperty(rating.ToString(CultureInfo.InvariantCulture)).GetInt32()));
    }

    [Fact]
    public async Task PublicFeedbackSummaryUsesCompleteEligiblePopulationWhenItemsAreLimited()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        var now = DateTimeOffset.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            for (var index = 0; index < 18; index++)
            {
                var email = $"public-summary-{index}@example.test";
                var user = new ApplicationUser
                {
                    Id = Guid.NewGuid(),
                    UserName = email,
                    NormalizedUserName = email.ToUpperInvariant(),
                    Email = email,
                    NormalizedEmail = email.ToUpperInvariant(),
                    EmailConfirmed = true,
                    SecurityStamp = Guid.NewGuid().ToString("N"),
                    ConcurrencyStamp = Guid.NewGuid().ToString("N"),
                    CreatedAt = now,
                    UpdatedAt = now,
                    IsActive = index < 17
                };
                var eligible = index < 15;
                db.ProductFeedbacks.Add(new ProductFeedback
                {
                    Id = Guid.NewGuid(),
                    User = user,
                    Rating = eligible ? (index % 5) + 1 : 5,
                    Comment = eligible ? $"Public comment {index}" : "Hidden comment",
                    Consent = eligible,
                    Status = eligible ? FeedbackValues.Approved : index == 15 ? FeedbackValues.Pending : FeedbackValues.Approved,
                    PublishedAt = eligible ? now : null,
                    DeletedAt = index == 17 ? now : null,
                    CreatedAt = now.AddMinutes(-index),
                    UpdatedAt = now.AddMinutes(-index)
                });
            }
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/api/v1/feedback/public?limit=3");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        Assert.Equal(15, data.GetProperty("ratingCount").GetInt32());
        Assert.Equal(3.0, data.GetProperty("averageRating").GetDouble(), precision: 10);
        Assert.Equal(3, data.GetProperty("items").GetArrayLength());
        Assert.DoesNotContain("userId", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<Account> RegisterAsync(HttpClient client, string email, string displayName)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<string> MakeAdminAsync(NexoraApiFactory factory, Guid userId, string email)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (!await roleManager.RoleExistsAsync(RoleNames.Admin))
                await roleManager.CreateAsync(new IdentityRole<Guid>(RoleNames.Admin));
            var user = await userManager.FindByIdAsync(userId.ToString());
            await userManager.AddToRoleAsync(user!, RoleNames.Admin);
        }

        using var client = factory.CreateHttpsClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
