using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        using (var profile = await client.PatchAsJsonAsync(
                   "/api/v1/me/profile", new { yearsOfExperience = 4 }))
            Assert.Equal(HttpStatusCode.OK, profile.StatusCode);

        using (var jd = await client.PostAsJsonAsync("/api/v1/job-descriptions", new
        {
            title = "Private role",
            content = "Private candidate requirements"
        })) Assert.Equal(HttpStatusCode.Created, jd.StatusCode);

        using (var careerGoal = await client.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = "Backend Developer",
            seniority = "senior"
        })) Assert.Equal(HttpStatusCode.Created, careerGoal.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            await SeedPendingInterviewAsync(db, account.UserId);
            db.RealtimeNotifications.Add(new Nexora.Data.Realtime.RealtimeNotification
            {
                UserId = account.UserId,
                ResourceType = "resume",
                ResourceId = Guid.NewGuid(),
                Status = "ready",
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.ProductFeedbacks.Add(new Nexora.Data.Feedback.ProductFeedback
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                Rating = 5,
                Comment = "Public feedback removed with account deletion.",
                Consent = true,
                Status = Nexora.Business.Feedback.FeedbackValues.Approved,
                PublishedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var export = await client.GetAsync("/api/v1/me/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var exported = await DataAsync(export);
        Assert.Equal(account.UserId, exported.GetProperty("profile").GetProperty("id").GetGuid());
        Assert.Equal(4, exported.GetProperty("profile").GetProperty("yearsOfExperience").GetInt32());
        Assert.Equal("Private role", exported.GetProperty("jobDescriptions")[0].GetProperty("title").GetString());
        Assert.Equal("Backend Developer", exported.GetProperty("careerGoals")[0].GetProperty("targetRole").GetString());
        Assert.Equal(5, exported.GetProperty("feedback")[0].GetProperty("rating").GetInt32());
        Assert.DoesNotContain("moderationStatus", exported.GetProperty("feedback")[0].GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", exported.GetRawText(), StringComparison.OrdinalIgnoreCase);
        using (var publicFeedback = await client.GetAsync("/api/v1/feedback/public"))
            Assert.Single((await DataAsync(publicFeedback)).EnumerateArray());

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
            Assert.Equal(0, await db.CareerGoals.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, await db.UserProfiles.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, await db.RefreshTokens.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, await db.RealtimeNotifications.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, await db.ProductFeedbacks.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(0, (await db.Entitlements.SingleAsync(item => item.UserId == account.UserId)).Reserved);
            Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Void));
            var user = await db.Users.SingleAsync(item => item.Id == account.UserId);
            Assert.NotNull(user.DeletedAt);
            Assert.EndsWith("@invalid.local", user.Email, StringComparison.Ordinal);
            Assert.Equal(PrivacyValues.Completed, (await db.DataPrivacyRequests.SingleAsync()).Status);
        }

        using var publicAfterDeletion = await client.GetAsync("/api/v1/feedback/public");
        Assert.Empty((await DataAsync(publicAfterDeletion)).EnumerateArray());
    }

    [Fact]
    public async Task DeletedResumeExportOmitsCurrentLibraryButRetainsSafeAnalysisHistory()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var now = DateTimeOffset.UtcNow;
        var resumeId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var storedFile = new StoredFile
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                StorageKey = "resumes/export-field-benchmark.pdf",
                FileName = "cv.pdf",
                ContentType = "application/pdf",
                Size = 100,
                Checksum = "export",
                CreatedAt = now
            };
            db.Add(new ResumeRecord
            {
                Id = resumeId,
                UserId = account.UserId,
                StoredFileId = storedFile.Id,
                StoredFile = storedFile,
                Status = PracticeValues.Ready,
                ExtractedText = "Backend engineer",
                StructuredProfile = "{\"summary\":\"Backend engineer\"}",
                ProfileModelVersion = "gemini-dev",
                ProfilePromptVersion = "resume-profile-v2",
                ProfileSchemaVersion = "resume-profile-v2",
                Version = 3,
                CreatedAt = now,
                UpdatedAt = now,
                DeletedAt = now
            });
            db.Add(new ResumeAnalysis
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                ResumeId = resumeId,
                ResumeVersion = 3,
                Mode = ResumeAnalysisModes.FieldBenchmark,
                ContextJson = "{\"mode\":\"field_benchmark\",\"industry\":\"Fintech\",\"targetRole\":\"Backend Engineer\",\"seniority\":\"senior\"}",
                Status = PracticeValues.Completed,
                ModelVersion = "gemini-dev",
                PromptVersion = "resume-analysis-field-benchmark-v2",
                SchemaVersion = "resume-analysis-field-benchmark-v2",
                RubricVersion = "analysis-field-benchmark-v2",
                ProfileSnapshot = "{\"private\":\"do-not-export\"}",
                ProfileModelVersion = "gemini-dev",
                ProfilePromptVersion = "resume-profile-v2",
                ProfileSchemaVersion = "resume-profile-v2",
                Result = "{\"readinessScore\":74}",
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = now
            });
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync("/api/v1/me/export");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var exported = await DataAsync(response);
        Assert.Empty(exported.GetProperty("resumes").EnumerateArray());
        Assert.Single(exported.GetProperty("analyses").EnumerateArray());
        var analysis = exported.GetProperty("analyses")[0];
        Assert.Equal("field_benchmark", analysis.GetProperty("mode").GetString());
        Assert.Equal("Fintech", analysis.GetProperty("context").GetProperty("industry").GetString());
        Assert.Equal(3, analysis.GetProperty("resumeVersion").GetInt32());
        Assert.Equal("analysis-field-benchmark-v2", analysis.GetProperty("rubricVersion").GetString());
        Assert.Equal("resume-profile-v2", analysis.GetProperty("profileSchemaVersion").GetString());
        Assert.DoesNotContain("ProfileSnapshot", exported.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do-not-export", exported.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletionRetainsUploadIntentUntilSignedCapabilityExpires()
    {
        var storage = new RecordingStorageProvider { RecreateOnFirstDelete = true };
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>(), services =>
        {
            services.RemoveAll<IStorageProvider>();
            services.AddSingleton<IStorageProvider>(storage);
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var now = DateTimeOffset.UtcNow;
        var pendingStorageKey = $"resumes/{account.UserId:N}/pending.pdf";
        var completedStorageKey = $"resumes/{account.UserId:N}/completed.pdf";

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.UploadIntents.AddRange(new UploadIntentRecord
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                TokenHash = "pending-intent-token-hash",
                StorageKey = pendingStorageKey,
                FileName = "pending.pdf",
                ContentType = "application/pdf",
                ExpectedSize = 128,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(10),
                Version = 1
            }, new UploadIntentRecord
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                TokenHash = "completed-intent-token-hash",
                StorageKey = completedStorageKey,
                FileName = "completed.pdf",
                ContentType = "application/pdf",
                ExpectedSize = 256,
                CreatedAt = now,
                ExpiresAt = now.AddMinutes(10),
                Version = 2,
                ActualSize = 256,
                Checksum = "completed-checksum",
                CompletedAt = now.AddMinutes(-1)
            });
            await db.SaveChangesAsync();
        }

        using var deletionRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        deletionRequest.Headers.Add("Idempotency-Key", "privacy-delete-upload-intent");
        using var deletion = await client.SendAsync(deletionRequest);
        Assert.Equal(HttpStatusCode.Accepted, deletion.StatusCode);

        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>().ProcessPendingAsync(CancellationToken.None));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var request = await db.DataPrivacyRequests.SingleAsync();
            Assert.Equal(PrivacyValues.Queued, request.Status);
            Assert.True(request.NextAttemptAt > now);
            Assert.Equal(2, await db.UploadIntents.CountAsync(item => item.UserId == account.UserId));
            Assert.Null((await db.Users.SingleAsync(item => item.Id == account.UserId)).DeletedAt);
            Assert.Contains(pendingStorageKey, storage.RecreatedKeys);
            Assert.Contains(completedStorageKey, storage.RecreatedKeys);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var intents = await db.UploadIntents.Where(item => item.UserId == account.UserId).ToArrayAsync();
            foreach (var intent in intents)
                intent.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            var request = await db.DataPrivacyRequests.SingleAsync();
            request.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>().ProcessPendingAsync(CancellationToken.None));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(0, await db.UploadIntents.CountAsync(item => item.UserId == account.UserId));
            Assert.Equal(PrivacyValues.Completed, (await db.DataPrivacyRequests.SingleAsync()).Status);
            Assert.NotNull((await db.Users.SingleAsync(item => item.Id == account.UserId)).DeletedAt);
        }

        Assert.Equal(2, storage.DeletedKeys.Count(key => key == pendingStorageKey));
        Assert.Equal(2, storage.DeletedKeys.Count(key => key == completedStorageKey));
        Assert.DoesNotContain(pendingStorageKey, storage.LiveKeys);
        Assert.DoesNotContain(completedStorageKey, storage.LiveKeys);
    }

    private static async Task SeedPendingInterviewAsync(NexoraDbContext db, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var entitlement = await db.Entitlements.SingleAsync(item => item.UserId == userId);
        entitlement.Reserved = 1;
        entitlement.UpdatedAt = now;

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
        db.AddRange(reservation, session);
        await db.SaveChangesAsync();
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"privacy-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Private candidate"
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

    private sealed class RecordingStorageProvider : IStorageProvider
    {
        public List<string> DeletedKeys { get; } = [];
        public HashSet<string> LiveKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> RecreatedKeys { get; } = new(StringComparer.Ordinal);
        public bool RecreateOnFirstDelete { get; init; }

        public Task<StoredObject> SaveAsync(Stream content, string fileName, string contentType, CancellationToken cancellationToken) =>
            Task.FromResult(new StoredObject("unused", fileName, contentType, 0));

        public Task<Stream> OpenReadAsync(string storageKey, CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(string storageKey, CancellationToken cancellationToken)
        {
            DeletedKeys.Add(storageKey);
            if (RecreateOnFirstDelete && DeletedKeys.Count(key => key == storageKey) == 1)
            {
                LiveKeys.Add(storageKey);
                RecreatedKeys.Add(storageKey);
            }
            else
            {
                LiveKeys.Remove(storageKey);
            }
            return Task.CompletedTask;
        }
    }
}
