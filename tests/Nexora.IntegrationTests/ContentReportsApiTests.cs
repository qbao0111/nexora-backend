using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Ai;
using Nexora.Business.Authorization;
using Nexora.Business.Billing;
using Nexora.Business.ContentReports;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Skills;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.ContentReports;
using Nexora.Data.Identity;
using Nexora.Data.Learning;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class ContentReportsApiTests
{
    [Fact]
    public async Task GrowthReportingRejectsUnavailableContentAndBoundsOversizedSnapshots()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client, "growth-bounds@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        using var malformed = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.LearningPath, contentId = "learning-path", reasonCode = ContentReportValues.Other
        });
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var emptyProfile = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.SkillProfile, contentId = Guid.NewGuid(), reasonCode = ContentReportValues.Other
        });
        Assert.Equal(HttpStatusCode.NotFound, emptyProfile.StatusCode);
        var pathId = await SeedGrowthAsync(factory, owner.UserId);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var path = await db.LearningPaths.SingleAsync(item => item.Id == pathId);
        var milestone = await db.LearningPathMilestones.SingleAsync(item => item.LearningPathId == pathId);
        var service = scope.ServiceProvider.GetRequiredService<IContentReportService>();
        var command = new SubmitContentReportCommand(ContentReportValues.LearningPath, pathId, ContentReportValues.Other, null);
        path.Status = LearningPathValues.Pending;
        await db.SaveChangesAsync();
        Assert.Equal("RESOURCE_NOT_FOUND", (await Assert.ThrowsAsync<Nexora.Business.Common.BusinessException>(
            () => service.SubmitAsync(owner.UserId, command, CancellationToken.None))).Code);
        path.Status = LearningPathValues.Active;
        var goal = await db.CareerGoals.SingleAsync(item => item.Id == path.CareerGoalId);
        goal.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        Assert.Equal("RESOURCE_NOT_FOUND", (await Assert.ThrowsAsync<Nexora.Business.Common.BusinessException>(
            () => service.SubmitAsync(owner.UserId, command, CancellationToken.None))).Code);
        goal.DeletedAt = null;
        db.LearningPathActivities.AddRange(Enumerable.Range(0, 100).Select(index => new LearningPathActivity
        {
            Id = Guid.NewGuid(), LearningPathId = pathId, LearningPathMilestoneId = milestone.Id,
            ActivityKey = $"large-{index}", Title = "Generated large activity", Description = new string('x', 500),
            Type = LearningPathValues.StarDrill, Status = LearningPathValues.Pending, SortOrder = index + 1,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        var receipt = await service.SubmitAsync(owner.UserId, command, CancellationToken.None);
        var snapshot = (await db.ContentReports.SingleAsync(item => item.Id == receipt.ReportId)).ContentSnapshot!;
        Assert.InRange(snapshot.Length, 1, ContentReportRules.MaximumSnapshotLength);
        using var json = JsonDocument.Parse(snapshot);
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.NotEmpty(json.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task GrowthReportingUsesOwnerScopedVersionedContent()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        await VerifyGrowthReportingAsync(factory);
    }

    internal static async Task VerifyGrowthReportingAsync(NexoraApiFactory factory)
    {
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client, "growth-report-owner@example.test");
        var other = await RegisterAsync(client, "growth-report-other@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        using var emptyResponse = await client.GetAsync("/api/v1/skill-profile");
        Assert.Equal(JsonValueKind.Null, (await DataAsync(emptyResponse)).GetProperty("reportingId").ValueKind);

        var ownerPath = await SeedGrowthAsync(factory, owner.UserId);
        var foreignPath = await SeedGrowthAsync(factory, other.UserId);
        using var profileResponse = await client.GetAsync("/api/v1/skill-profile");
        var profileId = (await DataAsync(profileResponse)).GetProperty("reportingId").GetGuid();
        Guid foreignProfileId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ISkillProfileService>();
            foreignProfileId = (await service.GetAsync(other.UserId, CancellationToken.None)).ReportingId!.Value;
        }
        Assert.NotEqual(profileId, foreignProfileId);
        foreach (var (type, id) in new[] { (ContentReportValues.LearningPath, ownerPath), (ContentReportValues.SkillProfile, profileId) })
        {
            using var submitted = await client.PostAsJsonAsync("/api/v1/content-reports", new
            {
                contentType = type, contentId = id, reasonCode = ContentReportValues.Inaccurate,
                description = "A user-initiated report.", contentSnapshot = "FORGED CLIENT CONTENT"
            });
            Assert.Equal(HttpStatusCode.Accepted, submitted.StatusCode);
            Assert.NotEqual(Guid.Empty, (await DataAsync(submitted)).GetProperty("reportId").GetGuid());
        }
        foreach (var (type, id) in new[] { (ContentReportValues.LearningPath, foreignPath), (ContentReportValues.SkillProfile, foreignProfileId) })
        {
            using var denied = await client.PostAsJsonAsync("/api/v1/content-reports", new
            {
                contentType = type, contentId = id, reasonCode = ContentReportValues.Other
            });
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        }
        string[] originalSnapshots;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            originalSnapshots = await db.ContentReports.Where(item => item.ReporterUserId == owner.UserId)
                .OrderBy(item => item.ContentType).Select(item => item.ContentSnapshot!).ToArrayAsync();
            Assert.Equal(2, originalSnapshots.Length);
            Assert.All(originalSnapshots, snapshot =>
            {
                Assert.InRange(snapshot.Length, 1, ContentReportRules.MaximumSnapshotLength);
                using var parsed = JsonDocument.Parse(snapshot);
                Assert.NotEmpty(parsed.RootElement.GetProperty("items").EnumerateArray());
                Assert.DoesNotContain("FORGED", snapshot, StringComparison.Ordinal);
                Assert.DoesNotContain("PRIVATE ANSWER", snapshot, StringComparison.Ordinal);
            });
            Assert.Contains(originalSnapshots, text => text.Contains("Generated learning", StringComparison.Ordinal));
            Assert.Contains(originalSnapshots, text => text.Contains("behavioral.action", StringComparison.Ordinal));
            var attempt = await db.StarAttempts.SingleAsync(item => item.UserId == owner.UserId);
            attempt.CompletedAt = attempt.CompletedAt!.Value.AddMinutes(1);
            var activity = await db.LearningPathActivities.SingleAsync(item => item.LearningPathId == ownerPath);
            activity.Description = "New content after report";
            await db.SaveChangesAsync();
        }
        using var stale = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.SkillProfile, contentId = profileId, reasonCode = ContentReportValues.Other
        });
        Assert.Equal(HttpStatusCode.NotFound, stale.StatusCode);
        using var updatedProfile = await client.GetAsync("/api/v1/skill-profile");
        Assert.NotEqual(profileId, (await DataAsync(updatedProfile)).GetProperty("reportingId").GetGuid());
        var adminToken = await MakeAdminAsync(factory, owner.UserId, "growth-report-owner@example.test");
        using var admin = factory.CreateHttpsClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var queue = await admin.GetAsync("/api/v1/admin/content-reports?contentType=skill_profile");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var entry = Assert.Single((await DataAsync(queue)).GetProperty("items").EnumerateArray());
        Assert.DoesNotContain("snapshot", entry.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var reportId = entry.GetProperty("id").GetGuid();
        using var detail = await admin.GetAsync($"/api/v1/admin/content-reports/{reportId}");
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        using var review = await admin.PostAsync($"/api/v1/admin/content-reports/{reportId}/review", null);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using var resolve = await admin.PostAsJsonAsync($"/api/v1/admin/content-reports/{reportId}/resolve",
            new { outcome = ContentReportValues.Resolved, resolutionCode = "content_corrected" });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        await using var finalScope = factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(originalSnapshots, await finalDb.ContentReports.OrderBy(item => item.ContentType)
            .Select(item => item.ContentSnapshot!).ToArrayAsync());
    }

    private static async Task<Guid> SeedGrowthAsync(NexoraApiFactory factory, Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var goal = new CareerGoal
        {
            Id = Guid.NewGuid(), UserId = userId, TargetRole = "Backend", Seniority = "junior",
            Active = true, CreatedAt = now, UpdatedAt = now
        };
        var path = new LearningPath
        {
            Id = Guid.NewGuid(), UserId = userId, CareerGoalId = goal.Id, Status = LearningPathValues.Active,
            CreatedAt = now, UpdatedAt = now
        };
        var milestone = new LearningPathMilestone
        {
            Id = Guid.NewGuid(), LearningPathId = path.Id, Code = LearningPathValues.DevelopingMilestone,
            Title = "Generated learning milestone", Status = LearningPathValues.Active, CreatedAt = now, UpdatedAt = now
        };
        var component = new StarComponentEvaluation(70, true, "Evidence", "Feedback");
        db.AddRange(goal, path, milestone, new LearningPathActivity
        {
            Id = Guid.NewGuid(), LearningPathId = path.Id, LearningPathMilestoneId = milestone.Id,
            ActivityKey = "behavioral.action", Type = LearningPathValues.StarDrill, Title = "Generated learning activity",
            Description = "Improve personal actions", Status = LearningPathValues.Pending, CreatedAt = now, UpdatedAt = now
        }, new StarAttempt
        {
            Id = Guid.NewGuid(), UserId = userId, Question = "PRIVATE QUESTION", Answer = "PRIVATE ANSWER",
            Status = PracticeFeatureValues.Completed,
            EvaluationJson = JsonSerializer.Serialize(new StarEvaluation(true, 70,
                component, component, component, component, [], ["strength"], ["tip"], AiOperations.ScoreScale),
                JsonSerializerOptions.Web),
            ModelVersion = "test-model", PromptVersion = "test-prompt", SchemaVersion = "test-schema",
            CreatedAt = now, UpdatedAt = now, CompletedAt = now
        });
        await db.SaveChangesAsync();
        return path.Id;
    }

    [Fact]
    public async Task OwnedAiContentCanBeReportedAndAdminListKeepsSnapshotOutOfQueue()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var reporter = await RegisterAsync(client, "content-reporter@example.test");
        var foreign = await RegisterAsync(client, "content-foreign@example.test");
        var contentId = Guid.NewGuid();
        await SeedStarEvaluationAsync(factory, reporter.UserId, contentId, "{\"feedback\":\"Clear generated feedback\"}");
        await SeedStarEvaluationAsync(factory, foreign.UserId, Guid.NewGuid(), "{\"feedback\":\"Foreign feedback\"}");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reporter.AccessToken);
        using var response = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId,
            reasonCode = ContentReportValues.Inaccurate,
            description = "This feedback is inaccurate.",
            reporterUserId = Guid.NewGuid(),
            contentSnapshot = "client-controlled snapshot must be ignored"
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var receipt = await DataAsync(response);
        var reportId = receipt.GetProperty("reportId").GetGuid();
        Assert.DoesNotContain("contentSnapshot", receipt.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var ownerSpoof = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId = await FindForeignAttemptIdAsync(factory, foreign.UserId),
            reasonCode = ContentReportValues.Inaccurate
        });
        Assert.Equal(HttpStatusCode.NotFound, ownerSpoof.StatusCode);
        using var unsupported = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = "unsupported_type",
            contentId,
            reasonCode = ContentReportValues.Other
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);

        var adminToken = await MakeAdminAsync(factory, reporter.UserId, "content-reporter@example.test");
        using var admin = factory.CreateHttpsClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var list = await admin.GetAsync("/api/v1/admin/content-reports?status=pending&contentType=star_evaluation&page=1&pageSize=10");
        Assert.True(list.StatusCode == HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        var page = await DataAsync(list);
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        var listed = page.GetProperty("items")[0];
        Assert.Equal(reporter.UserId, listed.GetProperty("reporterUserId").GetGuid());
        Assert.DoesNotContain("description", listed.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("snapshot", listed.GetRawText(), StringComparison.OrdinalIgnoreCase);

        using var detailResponse = await admin.GetAsync($"/api/v1/admin/content-reports/{reportId}");
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await DataAsync(detailResponse);
        Assert.Equal("This feedback is inaccurate.", detail.GetProperty("description").GetString());
        Assert.Contains("Clear generated feedback", detail.GetProperty("contentSnapshot").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("client-controlled", detail.GetProperty("contentSnapshot").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Private candidate answer", detail.GetProperty("contentSnapshot").GetString(), StringComparison.Ordinal);

        using var review = await admin.PostAsync($"/api/v1/admin/content-reports/{reportId}/review", null);
        Assert.Equal(HttpStatusCode.OK, review.StatusCode);
        using var resolve = await admin.PostAsJsonAsync($"/api/v1/admin/content-reports/{reportId}/resolve", new
        {
            outcome = ContentReportValues.Dismissed,
            resolutionCode = "no_action",
            resolutionNote = "Review completed."
        });
        Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
        Assert.Equal(ContentReportValues.Dismissed, (await DataAsync(resolve))
            .GetProperty("report").GetProperty("status").GetString());
        using var resolveRetry = await admin.PostAsJsonAsync($"/api/v1/admin/content-reports/{reportId}/resolve", new
        {
            outcome = ContentReportValues.Dismissed,
            resolutionCode = "no_action",
            resolutionNote = "Review completed."
        });
        Assert.Equal(HttpStatusCode.OK, resolveRetry.StatusCode);
        using var invalidTransition = await admin.PostAsync($"/api/v1/admin/content-reports/{reportId}/review", null);
        Assert.Equal(HttpStatusCode.Conflict, invalidTransition.StatusCode);
    }

    [Fact]
    public async Task ContentReportValidationAndAdminAuthorizationAreEnforced()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var user = await RegisterAsync(client, "content-validation@example.test");
        var contentId = Guid.NewGuid();
        await SeedStarEvaluationAsync(factory, user.UserId, contentId, "{\"score\":80}");
        var oversizedOutputId = Guid.NewGuid();
        await SeedStarEvaluationAsync(factory, user.UserId, oversizedOutputId,
            JsonSerializer.Serialize(new { text = new string('x', ContentReportRules.MaximumSnapshotLength) }));

        using var anonymous = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId,
            reasonCode = ContentReportValues.Other
        });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);

        using var missing = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId = Guid.NewGuid(),
            reasonCode = ContentReportValues.Other
        });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalidReason = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId,
            reasonCode = "made_up"
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidReason.StatusCode);
        using var longDescription = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId,
            reasonCode = ContentReportValues.Other,
            description = new string('x', ContentReportRules.MaximumDescriptionLength + 1)
        });
        Assert.Equal(HttpStatusCode.BadRequest, longDescription.StatusCode);
        using var longSnapshot = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId = oversizedOutputId,
            reasonCode = ContentReportValues.Other
        });
        Assert.Equal(HttpStatusCode.Conflict, longSnapshot.StatusCode);
        using var nonAdminList = await client.GetAsync("/api/v1/admin/content-reports");
        Assert.Equal(HttpStatusCode.Forbidden, nonAdminList.StatusCode);
        using var nonAdminReview = await client.PostAsync($"/api/v1/admin/content-reports/{Guid.NewGuid()}/review", null);
        Assert.Equal(HttpStatusCode.Forbidden, nonAdminReview.StatusCode);
    }

    [Fact]
    public async Task AdminQueueFiltersAndPaginatesDeterministically()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var email = "content-page@example.test";
        var account = await RegisterAsync(client, email);
        var now = DateTimeOffset.UtcNow;
        var reporterId = account.UserId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.ContentReports.AddRange(Enumerable.Range(0, 3).Select(index => new ContentReport
            {
                Id = Guid.Parse($"20000000-0000-0000-0000-{index + 1:000000000000}"),
                ReporterUserId = reporterId,
                ContentType = ContentReportValues.InterviewReport,
                ContentId = Guid.NewGuid(),
                ReasonCode = index == 1 ? ContentReportValues.PrivacyViolation : ContentReportValues.Inaccurate,
                ContentSnapshot = "{}",
                Status = ContentReportValues.Pending,
                CreatedAt = now.AddMinutes(-index)
            }));
            await db.SaveChangesAsync();
        }

        var adminToken = await MakeAdminAsync(factory, reporterId, email);
        using var admin = factory.CreateHttpsClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var response = await admin.GetAsync("/api/v1/admin/content-reports?status=pending&reasonCode=inaccurate&page=1&pageSize=1");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var page = await DataAsync(response);
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.GetProperty("items").GetArrayLength());
        Assert.Equal(now, page.GetProperty("items")[0].GetProperty("createdAt").GetDateTimeOffset());
        var from = Uri.EscapeDataString(now.AddSeconds(-30).ToString("O"));
        var to = Uri.EscapeDataString(now.AddSeconds(30).ToString("O"));
        using var dateFiltered = await admin.GetAsync($"/api/v1/admin/content-reports?from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, dateFiltered.StatusCode);
        Assert.Equal(1, (await DataAsync(dateFiltered)).GetProperty("totalCount").GetInt32());
        using var invalidPage = await admin.GetAsync("/api/v1/admin/content-reports?page=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
    }

    [Fact]
    public async Task ContentReportSubmissionIsRateLimitedPerAuthenticatedUser()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:ContentReport:PermitLimit"] = "2"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var user = await RegisterAsync(client, "content-rate-limit@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);
        var contentIds = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in contentIds)
            await SeedStarEvaluationAsync(factory, user.UserId, id, "{\"score\":80}");

        var statuses = new List<HttpStatusCode>();
        foreach (var contentId in contentIds)
        {
            using var response = await client.PostAsJsonAsync("/api/v1/content-reports", new
            {
                contentType = ContentReportValues.StarEvaluation,
                contentId,
                reasonCode = ContentReportValues.Other
            });
            statuses.Add(response.StatusCode);
        }

        Assert.Equal(new[] { HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests }, statuses);
    }

    [Fact]
    public async Task EverySupportedAiResourceResolvesToItsPersistedOutput()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["RateLimits:ContentReport:PermitLimit"] = "10"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var user = await RegisterAsync(client, "content-mapping@example.test");
        var resources = await SeedReportableResourcesAsync(factory, user.UserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", user.AccessToken);

        foreach (var (contentType, contentId) in resources)
        {
            using var response = await client.PostAsJsonAsync("/api/v1/content-reports", new
            {
                contentType,
                contentId,
                reasonCode = ContentReportValues.Other
            });
            Assert.True(response.StatusCode == HttpStatusCode.Accepted,
                $"{contentType}: {await response.Content.ReadAsStringAsync()}");
        }

        var adminToken = await MakeAdminAsync(factory, user.UserId, "content-mapping@example.test");
        using var admin = factory.CreateHttpsClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var queue = await admin.GetAsync("/api/v1/admin/content-reports?pageSize=10");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var items = (await DataAsync(queue)).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(resources.Count, items.Length);
        foreach (var item in items)
        {
            using var detail = await admin.GetAsync($"/api/v1/admin/content-reports/{item.GetProperty("id").GetGuid()}");
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            Assert.False(string.IsNullOrWhiteSpace((await DataAsync(detail)).GetProperty("contentSnapshot").GetString()));
        }
    }

    private static async Task SeedStarEvaluationAsync(NexoraApiFactory factory, Guid userId, Guid id, string evaluation)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        db.StarAttempts.Add(new StarAttempt
        {
            Id = id,
            UserId = userId,
            Question = "Private source question",
            Answer = "Private candidate answer",
            Status = PracticeFeatureValues.Completed,
            EvaluationJson = evaluation,
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> FindForeignAttemptIdAsync(NexoraApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        return await db.StarAttempts.Where(item => item.UserId == userId).Select(item => item.Id).SingleAsync();
    }

    private static async Task<Dictionary<string, Guid>> SeedReportableResourcesAsync(NexoraApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var entitlement = await db.Entitlements.SingleAsync(item => item.UserId == userId);
        var sessionId = Guid.NewGuid();
        var reservation = new UsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementId = entitlement.Id,
            Action = BillingValues.Reserve,
            Quantity = 1,
            SourceType = "interview",
            SourceId = sessionId.ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = now
        };
        var session = new InterviewSession
        {
            Id = sessionId,
            UserId = userId,
            ReservationEventId = reservation.Id,
            Role = "Backend Engineer",
            Seniority = "junior",
            InterviewType = "technical",
            Difficulty = "medium",
            Status = PracticeValues.Completed,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        var question = new InterviewQuestion
        {
            Id = Guid.NewGuid(),
            InterviewSessionId = sessionId,
            Sequence = 1,
            Kind = "primary",
            Topic = "technical",
            Content = "Generated interview question",
            PromptVersion = "test-prompt",
            ModelVersion = "test-model",
            CreatedAt = now,
            ReleasedAt = now
        };
        var answer = new InterviewAnswer
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InterviewSessionId = sessionId,
            QuestionId = question.Id,
            Content = "Private candidate answer",
            Evaluation = "{\"feedback\":\"Generated answer coaching\"}",
            EvaluationStatus = InterviewAnswerEvaluationStates.Ready,
            EvaluationCompletedAt = now,
            CreatedAt = now
        };
        var interviewReport = new InterviewReport
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InterviewSessionId = sessionId,
            OverallScore = 80,
            Rubric = "{\"score\":80}",
            Strengths = "[]",
            Gaps = "[]",
            ActionPlan = "[]",
            Disclaimer = "AI generated",
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            RubricVersion = "test-rubric",
            SchemaVersion = "test-schema",
            CreatedAt = now
        };
        var storedFile = new StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = $"resumes/{userId:N}/reportable.pdf",
            FileName = "resume.pdf",
            ContentType = "application/pdf",
            Size = 10,
            Checksum = "checksum",
            CreatedAt = now
        };
        var resume = new ResumeRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StoredFileId = storedFile.Id,
            Status = PracticeValues.Ready,
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var resumeAnalysis = new ResumeAnalysis
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ResumeId = resume.Id,
            ResumeVersion = 1,
            Mode = ResumeAnalysisModes.FieldBenchmark,
            Status = PracticeValues.Completed,
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            Result = "{\"readinessScore\":80}",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        var categoryId = await db.ScenarioCategories.Select(item => item.Id).FirstAsync();
        var scenario = new Scenario
        {
            Id = Guid.NewGuid(),
            Slug = $"content-report-{Guid.NewGuid():N}",
            Title = "Reportable scenario",
            Summary = "Test summary",
            CategoryId = categoryId,
            Difficulty = "medium",
            Competency = "analysis",
            EstimatedMinutes = 10,
            Content = "Scenario prompt",
            Status = PracticeFeatureValues.Published,
            CreatedAt = now,
            UpdatedAt = now,
            PublishedAt = now
        };
        var scenarioAttempt = new ScenarioAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ScenarioId = scenario.Id,
            Status = PracticeFeatureValues.Completed,
            Answer = "Private scenario answer",
            EvaluationJson = "{\"score\":80}",
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        var starAttempt = new StarAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Question = "Private STAR question",
            Answer = "Private STAR answer",
            Status = PracticeFeatureValues.Completed,
            EvaluationJson = "{\"score\":90}",
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        db.AddRange(reservation, session, question, answer, interviewReport, storedFile, resume, resumeAnalysis,
            scenario, scenarioAttempt, starAttempt);
        await db.SaveChangesAsync();
        return new Dictionary<string, Guid>
        {
            [ContentReportValues.InterviewQuestion] = question.Id,
            [ContentReportValues.InterviewAnswerEvaluation] = answer.Id,
            [ContentReportValues.InterviewReport] = interviewReport.Id,
            [ContentReportValues.ResumeAnalysis] = resumeAnalysis.Id,
            [ContentReportValues.ScenarioEvaluation] = scenarioAttempt.Id,
            [ContentReportValues.StarEvaluation] = starAttempt.Id
        };
    }

    private static async Task<Account> RegisterAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
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
            var user = await userManager.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("Test user missing.");
            await userManager.AddToRoleAsync(user, RoleNames.Admin);
        }
        using var client = factory.CreateHttpsClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        var data = await DataAsync(login);
        return data.GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
