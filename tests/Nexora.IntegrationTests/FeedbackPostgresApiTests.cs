using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Feedback;
using Nexora.Data.Feedback;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class FeedbackPostgresApiTests
{
    [PostgresFact]
    public async Task PublicFeedbackEndpointTranslatesAndOrdersOnPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        await using var factory = NexoraApiFactory.CreatePostgres(connectionString);
        factory.InitializeDatabase();
        var now = new DateTimeOffset(2026, 9, 22, 16, 0, 0, TimeSpan.Zero);
        var deterministicLowId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var deterministicHighId = Guid.Parse("10000000-0000-0000-0000-000000000002");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.ProductFeedbacks.AddRange(
                CreateFeedback("featured", "Featured Candidate", 5, now.AddDays(-20), now.AddDays(-10), featured: true),
                CreateFeedback("fallback", null, 4, now.AddHours(-1), null, updatedAt: now),
                CreateFeedback("deterministic-high", "Deterministic High", 3, now.AddDays(-2), now.AddDays(-1), id: deterministicHighId),
                CreateFeedback("deterministic-low", "Deterministic Low", 2, now.AddDays(-2), now.AddDays(-1), id: deterministicLowId),
                CreateFeedback("limited-out", "Limited Out", 1, now.AddDays(-4), now.AddDays(-3)),
                CreateFeedback("pending", "Pending", 5, now, now, status: FeedbackValues.Pending),
                CreateFeedback("rejected", "Rejected", 5, now, now, status: FeedbackValues.Rejected),
                CreateFeedback("no-consent", "No Consent", 5, now, now, consent: false),
                CreateFeedback("deleted-feedback", "Deleted Feedback", 5, now, now, feedbackDeletedAt: now),
                CreateFeedback("inactive-user", "Inactive", 5, now, now, userActive: false),
                CreateFeedback("deletion-requested", "Deletion Requested", 5, now, now, deletionRequestedAt: now),
                CreateFeedback("deleted-user", "Deleted User", 5, now, now, userDeletedAt: now));
            await db.SaveChangesAsync();
        }

        using var client = factory.CreateHttpsClient();
        using var response = await client.GetAsync("/api/v1/feedback/public?limit=4");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        Assert.Equal(5, data.GetProperty("ratingCount").GetInt32());
        Assert.Equal(3.0, data.GetProperty("averageRating").GetDouble(), precision: 10);
        var items = data.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(4, items.Length);
        Assert.Equal("Featured Candidate", items[0].GetProperty("displayName").GetString());
        Assert.Equal("Người dùng Nexora", items[1].GetProperty("displayName").GetString());
        Assert.Equal("Deterministic High", items[2].GetProperty("displayName").GetString());
        Assert.Equal("Deterministic Low", items[3].GetProperty("displayName").GetString());
        Assert.Equal(deterministicHighId, items[2].GetProperty("id").GetGuid());
        Assert.Equal(deterministicLowId, items[3].GetProperty("id").GetGuid());
        Assert.DoesNotContain("userId", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", data.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static ProductFeedback CreateFeedback(
        string suffix,
        string? displayName,
        int rating,
        DateTimeOffset createdAt,
        DateTimeOffset? publishedAt,
        bool featured = false,
        string status = FeedbackValues.Approved,
        bool consent = true,
        bool userActive = true,
        DateTimeOffset? deletionRequestedAt = null,
        DateTimeOffset? userDeletedAt = null,
        DateTimeOffset? feedbackDeletedAt = null,
        DateTimeOffset? updatedAt = null,
        Guid? id = null)
    {
        var userId = Guid.NewGuid();
        var email = $"public-postgres-{suffix}-{userId:N}@example.test";
        var user = new ApplicationUser
        {
            Id = userId,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt ?? createdAt,
            IsActive = userActive,
            DeletionRequestedAt = deletionRequestedAt,
            DeletedAt = userDeletedAt
        };
        if (displayName is not null)
        {
            user.Profile = new UserProfile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                User = user,
                DisplayName = displayName,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt ?? createdAt
            };
        }

        return new ProductFeedback
        {
            Id = id ?? Guid.NewGuid(),
            UserId = userId,
            User = user,
            Rating = rating,
            Comment = $"Public comment {suffix}",
            Consent = consent,
            Status = status,
            Featured = featured,
            CreatedAt = createdAt,
            UpdatedAt = updatedAt ?? createdAt,
            PublishedAt = publishedAt,
            DeletedAt = feedbackDeletedAt
        };
    }
}
