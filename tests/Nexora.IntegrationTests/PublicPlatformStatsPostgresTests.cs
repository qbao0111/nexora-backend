using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Billing;
using Nexora.Business.Feedback;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Feedback;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class PublicPlatformStatsPostgresTests
{
    [PostgresFact]
    public async Task PublicPlatformStatsUseCanonicalCompletedAndFeedbackSemanticsWithoutSensitiveFields()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        await using var factory = NexoraApiFactory.CreatePostgres(connectionString);
        factory.InitializeDatabase();
        var now = new DateTimeOffset(2026, 9, 23, 3, 0, 0, TimeSpan.Zero);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var activeUser = CreateUser("active", now);
            var secondActiveUser = CreateUser("second-active", now);
            var inactiveUser = CreateUser("inactive", now, isActive: false);
            var deletionRequestedUser = CreateUser("deletion-requested", now, deletionRequestedAt: now);
            var deletedUser = CreateUser("deleted", now, deletedAt: now);
            db.Users.AddRange(activeUser, secondActiveUser, inactiveUser, deletionRequestedUser, deletedUser);
            await db.SaveChangesAsync();

            db.ProductFeedbacks.AddRange(
                CreateFeedback(activeUser.Id, 5, now),
                CreateFeedback(secondActiveUser.Id, 3, now),
                CreateFeedback(inactiveUser.Id, 1, now),
                CreateFeedback(deletionRequestedUser.Id, 1, now),
                CreateFeedback(deletedUser.Id, 1, now),
                CreateFeedback(activeUser.Id, 1, now, status: FeedbackValues.Pending, deletedAt: now));

            var file = new StoredFile
            {
                Id = Guid.NewGuid(),
                UserId = activeUser.Id,
                StorageKey = $"stats/{Guid.NewGuid():N}",
                FileName = "cv.pdf",
                ContentType = "application/pdf",
                Size = 10,
                Checksum = Guid.NewGuid().ToString("N"),
                CreatedAt = now
            };
            var resume = new ResumeRecord
            {
                Id = Guid.NewGuid(),
                UserId = activeUser.Id,
                StoredFileId = file.Id,
                Status = PracticeValues.Ready,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.AddRange(file, resume);
            db.ResumeAnalyses.AddRange(
                CreateAnalysis(activeUser.Id, resume.Id, PracticeValues.Completed, now, result: "{}"),
                CreateAnalysis(activeUser.Id, resume.Id, PracticeValues.Processing, null),
                CreateAnalysis(activeUser.Id, resume.Id, PracticeValues.Failed, null),
                CreateAnalysis(activeUser.Id, resume.Id, PracticeValues.Completed, null, result: "{}"));

            var subscription = new Subscription
            {
                Id = Guid.NewGuid(),
                UserId = activeUser.Id,
                Status = BillingValues.Active,
                StartsAt = now.AddDays(-1),
                EndsAt = now.AddDays(30),
                CreatedAt = now,
                UpdatedAt = now
            };
            var entitlement = new Entitlement
            {
                Id = Guid.NewGuid(),
                UserId = activeUser.Id,
                SubscriptionId = subscription.Id,
                PlanCodeSnapshot = "stats-test",
                Status = BillingValues.Active,
                StartsAt = subscription.StartsAt,
                EndsAt = subscription.EndsAt,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid()
            };
            db.AddRange(subscription, entitlement);
            foreach (var (status, completedAt) in new[]
                     {
                         (PracticeValues.Completed, (DateTimeOffset?)now),
                         (PracticeValues.Active, null),
                         ("abandoned", null),
                         (PracticeValues.Completed, null)
                     })
            {
                var reservation = new UsageEvent
                {
                    Id = Guid.NewGuid(),
                    UserId = activeUser.Id,
                    EntitlementId = entitlement.Id,
                    Action = BillingValues.Consume,
                    Quantity = 1,
                    SourceType = "interview",
                    SourceId = Guid.NewGuid().ToString("N"),
                    IdempotencyKey = Guid.NewGuid().ToString("N"),
                    CreatedAt = now
                };
                db.AddRange(reservation, new InterviewSession
                {
                    Id = Guid.NewGuid(),
                    UserId = activeUser.Id,
                    ReservationEventId = reservation.Id,
                    Role = "Backend Developer",
                    Seniority = "senior",
                    InterviewType = "technical",
                    Difficulty = "medium",
                    Status = status,
                    Version = 1,
                    CreatedAt = now,
                    UpdatedAt = now,
                    CompletedAt = completedAt
                });
            }

            await db.SaveChangesAsync();
        }

        using var client = factory.CreateHttpsClient();
        using var statsResponse = await client.GetAsync("/api/v1/public/platform-stats");
        using var feedbackResponse = await client.GetAsync("/api/v1/feedback/public?limit=20");

        Assert.Equal(HttpStatusCode.OK, statsResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, feedbackResponse.StatusCode);
        using var statsDocument = JsonDocument.Parse(await statsResponse.Content.ReadAsStringAsync());
        using var feedbackDocument = JsonDocument.Parse(await feedbackResponse.Content.ReadAsStringAsync());
        var stats = statsDocument.RootElement.GetProperty("data");
        var feedback = feedbackDocument.RootElement.GetProperty("data");

        Assert.Equal(2, stats.GetProperty("userCount").GetInt32());
        Assert.Equal(1, stats.GetProperty("completedInterviewCount").GetInt32());
        Assert.Equal(1, stats.GetProperty("completedCvAnalysisCount").GetInt32());
        Assert.Equal(feedback.GetProperty("ratingCount").GetInt32(), stats.GetProperty("ratingCount").GetInt32());
        Assert.Equal(feedback.GetProperty("averageRating").GetDouble(), stats.GetProperty("averageRating").GetDouble(), precision: 10);
        Assert.Equal(2, stats.GetProperty("ratingCount").GetInt32());
        Assert.Equal(4.0, stats.GetProperty("averageRating").GetDouble(), precision: 10);

        var json = stats.GetRawText();
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("moderation", json, StringComparison.OrdinalIgnoreCase);
    }

    private static ApplicationUser CreateUser(
        string suffix,
        DateTimeOffset now,
        bool isActive = true,
        DateTimeOffset? deletionRequestedAt = null,
        DateTimeOffset? deletedAt = null)
    {
        var id = Guid.NewGuid();
        var email = $"platform-stats-{suffix}-{id:N}@example.test";
        return new ApplicationUser
        {
            Id = id,
            UserName = email,
            NormalizedUserName = email.ToUpperInvariant(),
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString("N"),
            ConcurrencyStamp = Guid.NewGuid().ToString("N"),
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = isActive,
            DeletionRequestedAt = deletionRequestedAt,
            DeletedAt = deletedAt
        };
    }

    private static ProductFeedback CreateFeedback(
        Guid userId,
        int rating,
        DateTimeOffset now,
        string status = FeedbackValues.Approved,
        DateTimeOffset? deletedAt = null) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Rating = rating,
            Comment = "Public feedback",
            Consent = true,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            PublishedAt = status == FeedbackValues.Approved ? now : null,
            DeletedAt = deletedAt
        };

    private static ResumeAnalysis CreateAnalysis(
        Guid userId,
        Guid resumeId,
        string status,
        DateTimeOffset? completedAt,
        string? result = null) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ResumeId = resumeId,
            ResumeVersion = 1,
            Mode = "field_benchmark",
            Status = status,
            ModelVersion = "test",
            PromptVersion = "test",
            SchemaVersion = "test",
            Result = result,
            CreatedAt = completedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = completedAt ?? DateTimeOffset.UtcNow,
            CompletedAt = completedAt
        };
}
