using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class ProductPlatformApiTests
{
    [Fact]
    public async Task NonAdminCannotCallAdminApi()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var response = await client.GetAsync("/api/v1/admin/users");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AdminUserDetailDoesNotExposePrivatePracticeContent()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var jd = new Nexora.Data.Practice.JobDescription
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            Title = "Secret JD",
            Content = "Secret JD body",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.JobDescriptions.Add(jd);
        await db.SaveChangesAsync();

        var admin = await MakeAdminAsync(factory, account.UserId);
        using var adminClient = factory.CreateHttpsClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
        using var response = await adminClient.GetAsync($"/api/v1/admin/users/{account.UserId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Secret JD body", body);
    }

    [Fact]
    public async Task ActiveEntitlementSnapshotDoesNotChangeWhenPlanPriceFeaturesChange()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);

        Guid planPriceId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            var plan = new Plan { Id = Guid.NewGuid(), Code = $"snap-{Guid.NewGuid():N}", Name = "Snapshot plan", IsActive = true, CreatedAt = now };
            var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 100_000, Currency = "VND", DurationDays = 14, InterviewQuota = 5, IsActive = true, CreatedAt = now };
            planPriceId = price.Id;
            var scenario = await db.FeatureDefinitions.SingleAsync(item => item.Code == FeatureValues.Scenario);
            var star = await db.FeatureDefinitions.SingleAsync(item => item.Code == FeatureValues.StarBuilder);
            db.AddRange(plan, price);
            db.PlanPriceFeatures.AddRange(
                new PlanPriceFeature { Id = Guid.NewGuid(), PlanPriceId = price.Id, FeatureDefinitionId = scenario.Id, IsEnabled = true, Limit = 10, CreatedAt = now, UpdatedAt = now },
                new PlanPriceFeature { Id = Guid.NewGuid(), PlanPriceId = price.Id, FeatureDefinitionId = star.Id, IsEnabled = true, Limit = null, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        var grant = await GrantPlanAsync(factory, account.UserId, planPriceId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var feature = await db.EntitlementFeatures.SingleAsync(item => item.EntitlementId == grant.EntitlementId && item.FeatureCode == FeatureValues.Scenario);
            Assert.Equal(10, feature.Limit);
        }

        // Admin mutates the plan price feature to a smaller limit; the existing entitlement snapshot must be unchanged.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var priceFeature = await db.PlanPriceFeatures.SingleAsync(item => item.PlanPriceId == planPriceId && item.FeatureDefinition.Code == FeatureValues.Scenario);
            priceFeature.Limit = 1;
            await db.SaveChangesAsync();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var feature = await db.EntitlementFeatures.SingleAsync(item => item.EntitlementId == grant.EntitlementId && item.FeatureCode == FeatureValues.Scenario);
            Assert.Equal(10, feature.Limit);
        }
    }

    [Fact]
    public async Task UnpublishedScenarioIsInvisibleToNormalUser()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var category = await db.ScenarioCategories.FirstAsync();
        db.Scenarios.Add(new Nexora.Data.Practice.Scenario
        {
            Id = Guid.NewGuid(),
            Slug = $"secret-{Guid.NewGuid():N}",
            Title = "Secret scenario",
            Summary = "Not published",
            CategoryId = category.Id,
            Difficulty = "medium",
            Competency = "problem",
            EstimatedMinutes = 10,
            Content = "Secret body",
            SortOrder = 99,
            Status = "draft",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        using var list = await client.GetAsync("/api/v1/scenarios");
        var body = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Secret scenario", body);
    }

    [Fact]
    public async Task ScenarioQuotaIsConsumedOnlyAfterSuccessfulCompletedEvaluation()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenario = await SeedPublishedScenarioAsync(factory);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.Scenario, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using (var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId = scenario.Id })
        })
        {
            create.Headers.Add("Idempotency-Key", "scenario-draft-first");
            using var createRes = await client.SendAsync(create);
            Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        }
        var attemptId = await CreateDraftAttemptAsync(client, scenario.Id);
        using (var submit = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
        {
            Content = JsonContent.Create(new { answer = "My structured scenario answer" })
        })
        {
            submit.Headers.Add("Idempotency-Key", "scenario-submit");
            Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(submit)).StatusCode);
        }
        await ProcessJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var ef = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(item => item.Entitlement.UserId == account.UserId && item.FeatureCode == FeatureValues.Scenario);
        Assert.Equal(1, ef.Consumed);
        Assert.Equal(0, ef.Reserved);
        var attempt = await db.ScenarioAttempts.SingleAsync(item => item.Id == attemptId);
        Assert.Equal(PracticeFeatureValues.Completed, attempt.Status);
    }

    [Fact]
    public async Task ScenarioFailureVoidsQuota()
    {
        using var factory = new NexoraApiFactory(new FailingAiProvider());
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenario = await SeedPublishedScenarioAsync(factory);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.Scenario, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var attemptId = await CreateDraftAttemptAsync(client, scenario.Id);
        using (var submit = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
        {
            Content = JsonContent.Create(new { answer = "Failing scenario answer" })
        })
        {
            submit.Headers.Add("Idempotency-Key", "scenario-fail");
            await client.SendAsync(submit);
        }
        await ProcessJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var ef = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(item => item.Entitlement.UserId == account.UserId && item.FeatureCode == FeatureValues.Scenario);
        Assert.Equal(0, ef.Consumed);
        Assert.Equal(0, ef.Reserved);
        var attempt = await db.ScenarioAttempts.SingleAsync(item => item.Id == attemptId);
        Assert.Equal(PracticeFeatureValues.Failed, attempt.Status);
    }

    [Fact]
    public async Task ScenarioSemanticInvalidTwiceVoidsQuotaWithoutFabricatedEvaluation()
    {
        var aiProvider = new TestAiProvider();
        var invalid = new ScenarioEvaluationResult(150, [], [], [], [], "", AiOperations.ScoreScale);
        aiProvider.EnqueueResponse(AiPurposes.ScenarioEvaluate, invalid);
        aiProvider.EnqueueResponse(AiPurposes.ScenarioEvaluate, invalid);
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenario = await SeedPublishedScenarioAsync(factory);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.Scenario, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var attemptId = await CreateDraftAttemptAsync(client, scenario.Id);
        using (var submit = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
        {
            Content = JsonContent.Create(new { answer = "A grounded scenario answer" })
        })
        {
            submit.Headers.Add("Idempotency-Key", "scenario-invalid-twice");
            using var response = await client.SendAsync(submit);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await ProcessJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var attempt = await db.ScenarioAttempts.SingleAsync(item => item.Id == attemptId);
        var feature = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId && item.FeatureCode == FeatureValues.Scenario);
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.ScenarioEvaluate));
        Assert.Equal(PracticeFeatureValues.Failed, attempt.Status);
        Assert.Null(attempt.EvaluationJson);
        Assert.Equal(0, feature.Reserved);
        Assert.Equal(0, feature.Consumed);
    }

    [Fact]
    public async Task StarAttemptQuotaIsConsumedOnlyAfterSuccessfulEvaluation()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.StarBuilder, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-attempts")
        {
            Content = JsonContent.Create(new { question = "Hãy kể về một lần bạn giải quyết xung đột.", answer = "Situation T Action R answer" })
        })
        {
            request.Headers.Add("Idempotency-Key", "star-create");
            var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var starId = json.RootElement.GetProperty("data").GetProperty("id").GetGuid();
            await ProcessJobsAsync(factory);

            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var ef = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(item => item.Entitlement.UserId == account.UserId && item.FeatureCode == FeatureValues.StarBuilder);
            Assert.Equal(1, ef.Consumed);
            var star = await db.StarAttempts.SingleAsync(item => item.Id == starId);
            Assert.Equal(PracticeFeatureValues.Completed, star.Status);
        }
    }

    [Fact]
    public async Task LimitedGenericFeatureCannotGoNegativeThroughAdjustment()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var admin = await MakeAdminAsync(factory, account.UserId);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.CvAnalysis, 1);
        using var adminClient = factory.CreateHttpsClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);

        using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/users/{account.UserId}/feature-adjustments")
        {
            Content = JsonContent.Create(new { featureCode = FeatureValues.CvAnalysis, quantity = -5, reason = "Negative test" })
        })
        {
            request.Headers.Add("Idempotency-Key", "adjust-negative");
            var response = await adminClient.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var ef = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(item => item.Entitlement.UserId == account.UserId && item.Entitlement.PlanCodeSnapshot != "free" && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(0, ef.Adjustment);
    }

    [Fact]
    public async Task ScenarioCreateNeverReturnsDraftOfAnotherScenario()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenarioA = await SeedPublishedScenarioAsync(factory);
        var scenarioB = await SeedPublishedScenarioAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var msgA = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId = scenarioA.Id })
        };
        msgA.Headers.Add("Idempotency-Key", "draft-A-key");
        using var resA = await client.SendAsync(msgA);
        Assert.Equal(HttpStatusCode.Created, resA.StatusCode);
        using var jsonA = JsonDocument.Parse(await resA.Content.ReadAsStringAsync());
        var attemptAId = jsonA.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var scAId = jsonA.RootElement.GetProperty("data").GetProperty("scenarioId").GetGuid();
        Assert.Equal(scenarioA.Id, scAId);

        using var msgB = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId = scenarioB.Id })
        };
        msgB.Headers.Add("Idempotency-Key", "draft-B-key");
        using var resB = await client.SendAsync(msgB);
        Assert.Equal(HttpStatusCode.Created, resB.StatusCode);
        using var jsonB = JsonDocument.Parse(await resB.Content.ReadAsStringAsync());
        var attemptBId = jsonB.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var scBId = jsonB.RootElement.GetProperty("data").GetProperty("scenarioId").GetGuid();
        Assert.NotEqual(attemptAId, attemptBId);
        Assert.Equal(scenarioB.Id, scBId);
    }

    [Fact]
    public async Task ScenarioCreateSameKeySameScenarioReturnsSameAttempt()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenario = await SeedPublishedScenarioAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var msg1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId = scenario.Id })
        };
        msg1.Headers.Add("Idempotency-Key", "same-key-same-sc");
        using var res1 = await client.SendAsync(msg1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        using var json1 = JsonDocument.Parse(await res1.Content.ReadAsStringAsync());
        var attempt1Id = json1.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var msg2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId = scenario.Id })
        };
        msg2.Headers.Add("Idempotency-Key", "same-key-same-sc");
        using var res2 = await client.SendAsync(msg2);
        Assert.Equal(HttpStatusCode.Created, res2.StatusCode);
        using var json2 = JsonDocument.Parse(await res2.Content.ReadAsStringAsync());
        var attempt2Id = json2.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        Assert.Equal(attempt1Id, attempt2Id);
    }

    [Fact]
    public async Task ScenarioCreateSameKeyDifferentScenarioReturnsConflict()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenarioA = await SeedPublishedScenarioAsync(factory);
        var scenarioB = await SeedPublishedScenarioAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var msg1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId = scenarioA.Id })
        };
        msg1.Headers.Add("Idempotency-Key", "conflict-key");
        using var res1 = await client.SendAsync(msg1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        using var msg2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId = scenarioB.Id })
        };
        msg2.Headers.Add("Idempotency-Key", "conflict-key");
        using var res2 = await client.SendAsync(msg2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
    }

    [Fact]
    public async Task StarAttemptReplayWithSameIdempotencyKeyDoesNotReserveTwice()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.StarBuilder, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        const string key = "star-replay-key";
        using var msg1 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-attempts")
        {
            Content = JsonContent.Create(new { question = "Question A", answer = "Answer A" })
        };
        msg1.Headers.Add("Idempotency-Key", key);
        using var res1 = await client.SendAsync(msg1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        using var msg2 = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-attempts")
        {
            Content = JsonContent.Create(new { question = "Question A", answer = "Answer A" })
        };
        msg2.Headers.Add("Idempotency-Key", key);
        using var res2 = await client.SendAsync(msg2);
        Assert.Equal(HttpStatusCode.Created, res2.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var ef = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(item => item.Entitlement.UserId == account.UserId && item.FeatureCode == FeatureValues.StarBuilder);
        Assert.Equal(1, ef.Reserved);
        Assert.Equal(0, ef.Consumed);
    }

    [Fact]
    public async Task CvAnalysisSuccessResultsInCompletedAndQuotaConsumed()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.CvAnalysis, 2);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var storedFile = new Nexora.Data.Practice.StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            StorageKey = "storage/key",
            FileName = "cv.pdf",
            ContentType = "application/pdf",
            Size = 1024,
            Checksum = "chk123",
            CreatedAt = now
        };
        var resume = new Nexora.Data.Practice.ResumeRecord
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            StoredFileId = storedFile.Id,
            StoredFile = storedFile,
            Status = PracticeValues.Ready,
            ExtractedText = "Experienced C# engineer with ASP.NET Core and PostgreSQL skills.",
            StructuredProfile = "{\"summary\":\"Experienced C# engineer\",\"skills\":[\"C#\",\"PostgreSQL\"],\"experiences\":[],\"education\":[],\"projects\":[],\"certifications\":[],\"languages\":[]}",
            ProfileModelVersion = "test-gemini-model",
            ProfilePromptVersion = "resume-profile-v2",
            ProfileSchemaVersion = "resume-profile-v2",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var jd = new Nexora.Data.Practice.JobDescription
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            Title = "Software Engineer",
            Content = "Requirements: C# and PostgreSQL",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(storedFile, resume, jd);
        await db.SaveChangesAsync();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new { resumeId = resume.Id, mode = "job_targeted", jobDescriptionId = jd.Id })
        };
        request.Headers.Add("Idempotency-Key", "cv-analysis-success");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var ef = await db2.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(item => item.Entitlement.UserId == account.UserId && item.Entitlement.PlanCodeSnapshot != "free" && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(1, ef.Reserved);
        Assert.Equal(0, ef.Consumed);

        await ProcessJobsAsync(factory);

        using var scope3 = factory.Services.CreateScope();
        var db3 = scope3.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var efAfter = await db3.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(item => item.Entitlement.UserId == account.UserId && item.Entitlement.PlanCodeSnapshot != "free" && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(0, efAfter.Reserved);
        Assert.Equal(1, efAfter.Consumed);
        var analysis = await db3.ResumeAnalyses.SingleAsync(item => item.UserId == account.UserId);
        Assert.Equal(PracticeValues.Completed, analysis.Status);
    }

    [Fact]
    public async Task CvFieldBenchmarkReusesCachedProfileAndPersistsContextAndVersions()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.CvAnalysis, 2);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        Guid resumeId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            var storedFile = new Nexora.Data.Practice.StoredFile
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                StorageKey = "storage/field-benchmark.pdf",
                FileName = "cv.pdf",
                ContentType = "application/pdf",
                Size = 1024,
                Checksum = "field-benchmark",
                CreatedAt = now
            };
            var resume = new Nexora.Data.Practice.ResumeRecord
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                StoredFileId = storedFile.Id,
                StoredFile = storedFile,
                Status = PracticeValues.Ready,
                ExtractedText = "C# and PostgreSQL skills.",
                StructuredProfile = "{\"summary\":\"Experienced backend engineer\",\"skills\":[\"C#\",\"PostgreSQL\"],\"experiences\":[],\"education\":[],\"projects\":[],\"certifications\":[],\"languages\":[]}",
                ProfileModelVersion = aiProvider.ModelVersion,
                ProfilePromptVersion = "resume-profile-v1",
                ProfileSchemaVersion = "resume-profile-v1",
                Version = 2,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.AddRange(storedFile, resume);
            await db.SaveChangesAsync();
            resumeId = resume.Id;
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new
            {
                resumeId,
                mode = "field_benchmark",
                industry = "Fintech",
                targetRole = "Backend Engineer",
                seniority = "senior"
            })
        })
        {
            request.Headers.Add("Idempotency-Key", "cv-analysis-field-benchmark");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var data = document.RootElement.GetProperty("data");
            Assert.Equal("field_benchmark", data.GetProperty("mode").GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("jobDescriptionVersion").ValueKind);
            Assert.Equal("Fintech", data.GetProperty("context").GetProperty("industry").GetString());
        }

        await ProcessJobsAsync(factory);

        await using (var firstScope = factory.Services.CreateAsyncScope())
        {
            var firstDb = firstScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var fieldAnalysis = await firstDb.ResumeAnalyses.SingleAsync(item => item.UserId == account.UserId);
            Assert.Equal(PracticeValues.Completed, fieldAnalysis.Status);
            Assert.Equal("field_benchmark", fieldAnalysis.Mode);
            Assert.Null(fieldAnalysis.JobDescriptionId);
            Assert.Null(fieldAnalysis.JobDescriptionVersion);
            Assert.Contains("Fintech", fieldAnalysis.ContextJson, StringComparison.Ordinal);
            Assert.NotNull(fieldAnalysis.ProfileSnapshot);
            Assert.Equal("test-gemini-model", fieldAnalysis.ProfileModelVersion);
            Assert.Equal("resume-profile-v2", fieldAnalysis.ProfilePromptVersion);
            Assert.Equal("resume-profile-v2", fieldAnalysis.ProfileSchemaVersion);
            Assert.Equal("resume-analysis-field-benchmark-v2", fieldAnalysis.PromptVersion);
            Assert.Equal("analysis-field-benchmark-v2", fieldAnalysis.RubricVersion);
            Assert.Equal("resume-analysis-field-benchmark-v2", fieldAnalysis.SchemaVersion);
            var readyResume = await firstDb.Resumes.SingleAsync(item => item.Id == resumeId);
            Assert.Equal("resume-profile-v2", readyResume.ProfilePromptVersion);
            Assert.Equal("resume-profile-v2", readyResume.ProfileSchemaVersion);
        }

        var jobDescriptionId = Guid.NewGuid();
        await using (var jdScope = factory.Services.CreateAsyncScope())
        {
            var jdDb = jdScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            jdDb.JobDescriptions.Add(new Nexora.Data.Practice.JobDescription
            {
                Id = jobDescriptionId,
                UserId = account.UserId,
                Title = "Backend Engineer",
                Content = "Requirements: C# and PostgreSQL",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await jdDb.SaveChangesAsync();
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new { resumeId, mode = "job_targeted", jobDescriptionId })
        })
        {
            request.Headers.Add("Idempotency-Key", "cv-analysis-job-after-field");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await ProcessJobsAsync(factory);

        await using var finalScope = factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var analyses = (await finalDb.ResumeAnalyses.Where(item => item.UserId == account.UserId).ToArrayAsync())
            .OrderBy(item => item.CreatedAt)
            .ToArray();
        Assert.Equal(2, analyses.Length);
        var finalFieldAnalysis = analyses.Single(item => item.Mode == "field_benchmark");
        var finalJobAnalysis = analyses.Single(item => item.Mode == "job_targeted");
        Assert.Equal(PracticeValues.Completed, finalFieldAnalysis.Status);
        Assert.Equal(PracticeValues.Completed, finalJobAnalysis.Status);
        Assert.Null(finalFieldAnalysis.JobDescriptionId);
        Assert.Equal(jobDescriptionId, finalJobAnalysis.JobDescriptionId);
        Assert.NotNull(finalFieldAnalysis.ProfileSnapshot);
        Assert.NotNull(finalJobAnalysis.ProfileSnapshot);
        Assert.All(analyses, item =>
        {
            Assert.Equal("test-gemini-model", item.ProfileModelVersion);
            Assert.Equal("resume-profile-v2", item.ProfilePromptVersion);
            Assert.Equal("resume-profile-v2", item.ProfileSchemaVersion);
        });
        var feature = await finalDb.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId
                && item.Entitlement.PlanCodeSnapshot != "free"
                && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(0, feature.Reserved);
        Assert.Equal(2, feature.Consumed);
        Assert.Equal(2, await finalDb.FeatureUsageEvents.CountAsync(item =>
            item.UserId == account.UserId
                && item.FeatureCode == FeatureValues.CvAnalysis
                && item.Action == FeatureValues.Consume));
        using var result = JsonDocument.Parse(finalFieldAnalysis.Result!);
        Assert.Equal(74, result.RootElement.GetProperty("readinessScore").GetInt32());
        Assert.True(result.RootElement.GetProperty("breakdown").TryGetProperty("roleAlignment", out _));
        Assert.Equal(1, aiProvider.GetCallCount(AiPurposes.ResumeProfile));
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.ResumeAnalysis));
    }

    [Theory]
    [InlineData("job_targeted")]
    [InlineData("field_benchmark")]
    public async Task FreeCvAnalysisQuotaIsSharedAcrossModes(string firstMode)
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var fixture = await SeedReadyResumeAsync(factory, account.UserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using (var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(CreateAnalysisPayload(fixture, firstMode))
        })
        {
            firstRequest.Headers.Add("Idempotency-Key", $"free-cv-first-{firstMode}");
            using var response = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await ProcessJobsAsync(factory);

        var secondMode = firstMode == ResumeAnalysisModes.JobTargeted
            ? ResumeAnalysisModes.FieldBenchmark
            : ResumeAnalysisModes.JobTargeted;
        using (var secondRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(CreateAnalysisPayload(fixture, secondMode))
        })
        {
            secondRequest.Headers.Add("Idempotency-Key", $"free-cv-second-{firstMode}");
            using var response = await client.SendAsync(secondRequest);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("FEATURE_QUOTA_EXCEEDED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var feature = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId
                && item.Entitlement.PlanCodeSnapshot == "free"
                && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(0, feature.Reserved);
        Assert.Equal(1, feature.Consumed);
        Assert.Equal(1, await db.ResumeAnalyses.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(1, await db.OutboxEvents.CountAsync(item => item.Type == "ResumeAnalysisRequested"));
        Assert.Equal(1, await db.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis && item.Action == FeatureValues.Reserve));
        Assert.Equal(1, await db.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis && item.Action == FeatureValues.Consume));
    }

    [Fact]
    public async Task FreeCvAnalysisFailureVoidsAllowanceBeforeAnotherModeSucceeds()
    {
        var aiProvider = new TestAiProvider();
        aiProvider.EnqueueResponse(AiPurposes.ResumeAnalysis, new TimeoutException("Deterministic first failure."));
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var fixture = await SeedReadyResumeAsync(factory, account.UserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using (var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(CreateAnalysisPayload(fixture, ResumeAnalysisModes.JobTargeted))
        })
        {
            firstRequest.Headers.Add("Idempotency-Key", "free-cv-failure");
            using var response = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await ProcessJobsAsync(factory);

        await using (var failedScope = factory.Services.CreateAsyncScope())
        {
            var failedDb = failedScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var failedFeature = await failedDb.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
                item => item.Entitlement.UserId == account.UserId
                    && item.Entitlement.PlanCodeSnapshot == "free"
                    && item.FeatureCode == FeatureValues.CvAnalysis);
            Assert.Equal(0, failedFeature.Reserved);
            Assert.Equal(0, failedFeature.Consumed);
            Assert.Equal(1, await failedDb.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis && item.Action == FeatureValues.Reserve));
            Assert.Equal(1, await failedDb.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis && item.Action == FeatureValues.Void));
            Assert.Equal(PracticeValues.Failed, (await failedDb.ResumeAnalyses.SingleAsync(item => item.UserId == account.UserId)).Status);
        }

        using (var retryRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(CreateAnalysisPayload(fixture, ResumeAnalysisModes.FieldBenchmark))
        })
        {
            retryRequest.Headers.Add("Idempotency-Key", "free-cv-after-failure");
            using var response = await client.SendAsync(retryRequest);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await ProcessJobsAsync(factory);

        await using var finalScope = factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var finalFeature = await finalDb.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId
                && item.Entitlement.PlanCodeSnapshot == "free"
                && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(0, finalFeature.Reserved);
        Assert.Equal(1, finalFeature.Consumed);
        Assert.Equal(2, await finalDb.ResumeAnalyses.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(1, await finalDb.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis && item.Action == FeatureValues.Consume));
    }

    [Fact]
    public async Task FreeCvAnalysisSameKeyReplayDoesNotReserveTwice()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var fixture = await SeedReadyResumeAsync(factory, account.UserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var key = "free-cv-replay";
        var payload = CreateAnalysisPayload(fixture, ResumeAnalysisModes.JobTargeted);

        Guid firstId;
        using (var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(payload)
        })
        {
            firstRequest.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            firstId = body.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        }

        using (var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(payload)
        })
        {
            replayRequest.Headers.Add("Idempotency-Key", key);
            using var response = await client.SendAsync(replayRequest);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(firstId, body.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        }

        await ProcessJobsAsync(factory);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.ResumeAnalyses.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(1, await db.OutboxEvents.CountAsync(item => item.Type == "ResumeAnalysisRequested"));
        Assert.Equal(1, await db.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis && item.Action == FeatureValues.Reserve));
        Assert.Equal(1, await db.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis && item.Action == FeatureValues.Consume));
    }

    [Fact]
    public async Task PaidCvAnalysisLimitUsesConfiguredPlanFeature()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);

        Guid planPriceId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            var plan = new Plan
            {
                Id = Guid.NewGuid(),
                Code = $"paid-cv-{Guid.NewGuid():N}",
                Name = "Paid CV test",
                IsActive = true,
                CreatedAt = now
            };
            var price = new PlanPrice
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                AmountMinor = 150_000,
                Currency = "VND",
                DurationDays = 30,
                InterviewQuota = 3,
                IsActive = true,
                CreatedAt = now
            };
            var cvAnalysis = await db.FeatureDefinitions.SingleAsync(item => item.Code == FeatureValues.CvAnalysis);
            db.AddRange(plan, price);
            db.PlanPriceFeatures.Add(new PlanPriceFeature
            {
                Id = Guid.NewGuid(),
                PlanPriceId = price.Id,
                FeatureDefinitionId = cvAnalysis.Id,
                IsEnabled = true,
                Limit = 2,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
            planPriceId = price.Id;
        }

        var grant = await GrantPlanAsync(factory, account.UserId, planPriceId);
        var fixture = await SeedReadyResumeAsync(factory, account.UserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        for (var index = 0; index < 2; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
            {
                Content = JsonContent.Create(CreateAnalysisPayload(fixture, ResumeAnalysisModes.JobTargeted))
            };
            request.Headers.Add("Idempotency-Key", $"paid-cv-configured-{index}");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            await ProcessJobsAsync(factory);
        }

        using (var rejected = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(CreateAnalysisPayload(fixture, ResumeAnalysisModes.FieldBenchmark))
        })
        {
            rejected.Headers.Add("Idempotency-Key", "paid-cv-configured-rejected");
            using var response = await client.SendAsync(rejected);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("FEATURE_QUOTA_EXCEEDED", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        await using var verifyScope = factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var feature = await verifyDb.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.EntitlementId == grant.EntitlementId && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.StartsWith("paid-cv-", feature.Entitlement.PlanCodeSnapshot);
        Assert.Equal(2, feature.Limit);
        Assert.Equal(0, feature.Reserved);
        Assert.Equal(2, feature.Consumed);
        Assert.Equal(2, await verifyDb.ResumeAnalyses.CountAsync(item => item.UserId == account.UserId));
    }

    [Fact]
    public async Task FreeCvAnalysisConcurrentDistinctKeysCannotExceedAllowance()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var fixture = await SeedReadyResumeAsync(factory, account.UserId);
        using var client1 = factory.CreateHttpsClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var client2 = factory.CreateHttpsClient();
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var task1 = Task.Run(() => SendResumeAnalysisAsync(client1, fixture, ResumeAnalysisModes.JobTargeted, "free-cv-concurrent-1"));
        var task2 = Task.Run(() => SendResumeAnalysisAsync(client2, fixture, ResumeAnalysisModes.FieldBenchmark, "free-cv-concurrent-2"));
        using var response1 = await task1;
        using var response2 = await task2;

        var responses = new[] { response1, response2 };
        Assert.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(item => item.StatusCode == HttpStatusCode.Forbidden));
        var rejected = responses.Single(item => item.StatusCode == HttpStatusCode.Forbidden);
        using (var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync()))
            Assert.Equal("FEATURE_QUOTA_EXCEEDED", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.ResumeAnalyses.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(1, await db.OutboxEvents.CountAsync(item => item.Type == "ResumeAnalysisRequested"));
        Assert.Equal(1, await db.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis && item.Action == FeatureValues.Reserve));
        var feature = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId
                && item.Entitlement.PlanCodeSnapshot == "free"
                && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(1, feature.Reserved);
    }

    [Fact]
    public async Task CvAnalysisRejectsInvalidModeContextBeforeCreatingAJob()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new
            {
                resumeId = Guid.NewGuid(),
                mode = "field_benchmark",
                industry = "Fintech",
                targetRole = "Backend Engineer"
            })
        };
        request.Headers.Add("Idempotency-Key", "cv-analysis-invalid-context");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("RESUME_ANALYSIS_CONTEXT_INVALID", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.ResumeAnalyses.Where(item => item.UserId == account.UserId).ToArrayAsync());
    }

    [Fact]
    public async Task CvAnalysisRejectsMixedModeContextsWithoutSideEffects()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var requests = new (string Key, object Payload)[]
        {
            ("cv-analysis-mixed-field", new
                {
                    resumeId = Guid.NewGuid(),
                    mode = "field_benchmark",
                    jobDescriptionId = Guid.NewGuid(),
                    industry = "Fintech",
                    targetRole = "Backend Engineer",
                    seniority = "senior"
                }),
            ("cv-analysis-mixed-job", new
                {
                    resumeId = Guid.NewGuid(),
                    mode = "job_targeted",
                    jobDescriptionId = Guid.NewGuid(),
                    industry = "Fintech",
                    targetRole = (string?)null,
                    seniority = (string?)null
                })
        };

        foreach (var item in requests)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
            {
                Content = JsonContent.Create(item.Payload)
            };
            request.Headers.Add("Idempotency-Key", item.Key);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("RESUME_ANALYSIS_CONTEXT_INVALID", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.ResumeAnalyses.Where(item => item.UserId == account.UserId).ToArrayAsync());
        Assert.Empty(await db.OutboxEvents.Where(item => item.AggregateId != Guid.Empty).ToArrayAsync());
        Assert.Empty(await db.FeatureUsageEvents.Where(item => item.UserId == account.UserId).ToArrayAsync());
    }

    [Fact]
    public async Task CvAnalysisIdempotencyScopesModeAndNormalizesContext()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.CvAnalysis, 2);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var resumeId = Guid.NewGuid();
        var jobDescriptionId = Guid.NewGuid();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            var storedFile = new Nexora.Data.Practice.StoredFile
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                StorageKey = "storage/idempotency.pdf",
                FileName = "cv.pdf",
                ContentType = "application/pdf",
                Size = 100,
                Checksum = "idempotency",
                CreatedAt = now
            };
            db.Add(new Nexora.Data.Practice.ResumeRecord
            {
                Id = resumeId,
                UserId = account.UserId,
                StoredFileId = storedFile.Id,
                StoredFile = storedFile,
                Status = PracticeValues.Ready,
                ExtractedText = "C# backend engineer",
                StructuredProfile = "{\"summary\":\"Backend engineer\",\"skills\":[\"C#\"],\"experiences\":[],\"education\":[],\"projects\":[],\"certifications\":[],\"languages\":[]}",
                ProfileModelVersion = "test-gemini-model",
                ProfilePromptVersion = "resume-profile-v2",
                ProfileSchemaVersion = "resume-profile-v2",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.Add(new Nexora.Data.Practice.JobDescription
            {
                Id = jobDescriptionId,
                UserId = account.UserId,
                Title = "Backend Engineer",
                Content = "C# and PostgreSQL",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using (var firstRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new
            {
                resumeId,
                mode = "field_benchmark",
                industry = "Fintech",
                targetRole = "Backend Engineer",
                seniority = "senior"
            })
        })
        {
            firstRequest.Headers.Add("Idempotency-Key", "analysis-mode-scope");
            using var firstResponse = await client.SendAsync(firstRequest);
            Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        }

        using (var conflictingRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new { resumeId, mode = "job_targeted", jobDescriptionId })
        })
        {
            conflictingRequest.Headers.Add("Idempotency-Key", "analysis-mode-scope");
            using var conflictResponse = await client.SendAsync(conflictingRequest);
            Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
            using var body = JsonDocument.Parse(await conflictResponse.Content.ReadAsStringAsync());
            Assert.Equal("IDEMPOTENCY_CONFLICT", body.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        Guid replayedAnalysisId;
        using (var normalizedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new
            {
                resumeId,
                mode = " field_benchmark ",
                industry = "  Fintech  ",
                targetRole = " Backend Engineer ",
                seniority = " senior "
            })
        })
        {
            normalizedRequest.Headers.Add("Idempotency-Key", "analysis-normalized-replay");
            using var normalizedResponse = await client.SendAsync(normalizedRequest);
            Assert.Equal(HttpStatusCode.Created, normalizedResponse.StatusCode);
            using var body = JsonDocument.Parse(await normalizedResponse.Content.ReadAsStringAsync());
            replayedAnalysisId = body.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        }

        using (var replayRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new
            {
                resumeId,
                mode = "field_benchmark",
                industry = "Fintech",
                targetRole = "Backend Engineer",
                seniority = "senior"
            })
        })
        {
            replayRequest.Headers.Add("Idempotency-Key", "analysis-normalized-replay");
            using var replayResponse = await client.SendAsync(replayRequest);
            Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
            using var body = JsonDocument.Parse(await replayResponse.Content.ReadAsStringAsync());
            Assert.Equal(replayedAnalysisId, body.RootElement.GetProperty("data").GetProperty("id").GetGuid());
        }

        await using var finalScope = factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(2, await finalDb.ResumeAnalyses.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(2, await finalDb.OutboxEvents.CountAsync(item => item.Type == "ResumeAnalysisRequested"));
        Assert.Equal(2, await finalDb.FeatureUsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == FeatureValues.Reserve));
    }

    [Fact]
    public async Task CvAnalysisRejectsUnknownModeWithoutSideEffects()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new { resumeId = Guid.NewGuid(), mode = "unknown" })
        };
        request.Headers.Add("Idempotency-Key", "analysis-unknown-mode");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("RESUME_ANALYSIS_MODE_INVALID", body.RootElement.GetProperty("error").GetProperty("code").GetString());

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.ResumeAnalyses.Where(item => item.UserId == account.UserId).ToArrayAsync());
        Assert.Empty(await db.OutboxEvents.Where(item => item.AggregateId != Guid.Empty).ToArrayAsync());
        Assert.Empty(await db.FeatureUsageEvents.Where(item => item.UserId == account.UserId).ToArrayAsync());
    }

    [Fact]
    public async Task CvAnalysisSemanticInvalidTwiceFailsAndVoidsQuotaWithoutFabricatedResult()
    {
        var aiProvider = new TestAiProvider();
        var invalid = new ResumeAnalysisOutput(
            [],
            ["Grounded gap"],
            ["Grounded recommendation"],
            MatchScore: 60,
            Summary: "Grounded summary",
            MatchedKeywordsOrSkills: [],
            MissingKeywordsOrSkills: [],
            SectionFeedback: ["Grounded section feedback."],
            Breakdown: new Dictionary<string, int>
            {
                ["technicalSkillMatch"] = 60,
                ["experienceRelevance"] = 60,
                ["impactEvidence"] = 60,
                ["clarity"] = 60,
                ["structure"] = 60
            },
            Mode: ResumeAnalysisModes.JobTargeted);
        aiProvider.EnqueueResponse(AiPurposes.ResumeAnalysis, invalid);
        aiProvider.EnqueueResponse(AiPurposes.ResumeAnalysis, invalid);
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.CvAnalysis, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        Guid resumeId;
        Guid jobDescriptionId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            var storedFile = new Nexora.Data.Practice.StoredFile
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                StorageKey = "storage/semantic-invalid.pdf",
                FileName = "cv.pdf",
                ContentType = "application/pdf",
                Size = 1024,
                Checksum = "semantic-invalid",
                CreatedAt = now
            };
            var resume = new Nexora.Data.Practice.ResumeRecord
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                StoredFileId = storedFile.Id,
                StoredFile = storedFile,
                Status = PracticeValues.Ready,
                ExtractedText = "C# and PostgreSQL skills.",
                StructuredProfile = "{\"summary\":null,\"skills\":[\"C#\"],\"experiences\":[],\"education\":[],\"projects\":[],\"certifications\":[],\"languages\":[]}",
                ProfileModelVersion = aiProvider.ModelVersion,
                ProfilePromptVersion = "resume-profile-v2",
                ProfileSchemaVersion = "resume-profile-v2",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            var jobDescription = new Nexora.Data.Practice.JobDescription
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                Title = "Backend Engineer",
                Content = "C# and PostgreSQL",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.AddRange(storedFile, resume, jobDescription);
            await db.SaveChangesAsync();
            resumeId = resume.Id;
            jobDescriptionId = jobDescription.Id;
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new { resumeId, mode = "job_targeted", jobDescriptionId })
        })
        {
            request.Headers.Add("Idempotency-Key", "cv-analysis-semantic-invalid");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        await ProcessJobsAsync(factory);

        await using var finalScope = factory.Services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var analysis = await finalDb.ResumeAnalyses.SingleAsync(item => item.UserId == account.UserId);
        var feature = await finalDb.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId
                && item.Entitlement.PlanCodeSnapshot != "free"
                && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.ResumeAnalysis));
        Assert.Equal(PracticeValues.Failed, analysis.Status);
        Assert.Null(analysis.Result);
        Assert.Equal(0, feature.Reserved);
        Assert.Equal(0, feature.Consumed);
    }

    [Fact]
    public async Task MeEndpointContainsExactlyOneInterviewFeatureFromCanonicalEntitlement()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.CvAnalysis, 3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var response = await client.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = doc.RootElement.GetProperty("data").GetProperty("billing").GetProperty("entitlement").GetProperty("features").EnumerateArray().ToArray();
        var interviewFeatures = features.Where(f => f.GetProperty("code").GetString() == FeatureValues.Interview).ToArray();
        Assert.Single(interviewFeatures);
        Assert.Equal(1, interviewFeatures[0].GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task PlanPriceWithHistoricalOrderCanBeDeactivatedButCommercialFieldsAreImmutable()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var admin = await MakeAdminAsync(factory, account.UserId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"price-test-{Guid.NewGuid():N}", Name = "Price test", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 50000, Currency = "VND", DurationDays = 30, InterviewQuota = 5, IsActive = true, CreatedAt = now };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            PlanPriceId = price.Id,
            PlanCodeSnapshot = plan.Code,
            AmountMinor = 50000,
            Currency = "VND",
            DurationDays = 30,
            InterviewQuota = 5,
            Status = BillingValues.Fulfilled,
            PaymentProvider = "test",
            ProviderTransactionId = "tx-1",
            CheckoutUrl = "https://example.test",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(plan, price, order);
        await db.SaveChangesAsync();

        using var adminClient = factory.CreateHttpsClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);

        using var changeAmountRes = await adminClient.PatchAsJsonAsync($"/api/v1/admin/plan-prices/{price.Id}", new
        {
            amountMinor = 99000,
            currency = "VND",
            durationDays = 30,
            interviewQuota = 5,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.Conflict, changeAmountRes.StatusCode);

        using var deactivateRes = await adminClient.PatchAsJsonAsync($"/api/v1/admin/plan-prices/{price.Id}", new
        {
            amountMinor = 50000,
            currency = "VND",
            durationDays = 30,
            interviewQuota = 5,
            isActive = false
        });
        Assert.Equal(HttpStatusCode.OK, deactivateRes.StatusCode);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var updatedPrice = await db2.PlanPrices.SingleAsync(item => item.Id == price.Id);
        Assert.False(updatedPrice.IsActive);
        Assert.Equal(50000, updatedPrice.AmountMinor);
    }

    [Fact]
    public async Task AdminScenarioUpdatePreservesSlugAndUpdatesFields()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var admin = await MakeAdminAsync(factory, account.UserId);

        using var adminClient = factory.CreateHttpsClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);

        using var catRes = await adminClient.GetAsync("/api/v1/admin/scenario-categories");
        using var catDoc = JsonDocument.Parse(await catRes.Content.ReadAsStringAsync());
        var categoryId = catDoc.RootElement.GetProperty("data")[0].GetProperty("id").GetGuid();

        var origSlug = $"scenario-edit-{Guid.NewGuid():N}";
        using var createRes = await adminClient.PostAsJsonAsync("/api/v1/admin/scenarios", new
        {
            slug = origSlug,
            title = "Original Title",
            summary = "Original summary",
            categoryId,
            difficulty = "easy",
            competency = "teamwork",
            estimatedMinutes = 10,
            content = "Original content"
        });
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        using var createDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var scenarioId = createDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        using var updateRes = await adminClient.PatchAsJsonAsync($"/api/v1/admin/scenarios/{scenarioId}", new
        {
            title = "Updated Title",
            summary = "Updated summary",
            categoryId,
            difficulty = "hard",
            competency = "leadership",
            estimatedMinutes = 25,
            content = "Updated content for the scenario."
        });
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        using var updateDoc = JsonDocument.Parse(await updateRes.Content.ReadAsStringAsync());
        var data = updateDoc.RootElement.GetProperty("data");

        Assert.Equal(origSlug, data.GetProperty("slug").GetString());
        Assert.Equal("Updated Title", data.GetProperty("title").GetString());
        Assert.Equal("Updated summary", data.GetProperty("summary").GetString());
        Assert.Equal("hard", data.GetProperty("difficulty").GetString());
        Assert.Equal("leadership", data.GetProperty("competency").GetString());
        Assert.Equal(25, data.GetProperty("estimatedMinutes").GetInt32());
        Assert.Equal("Updated content for the scenario.", data.GetProperty("content").GetString());
    }

    [Fact]
    public async Task ScenarioSubmitSameKeySameAnswerReplaysDifferentAnswerConflicts()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.Scenario, 5);
        var scenario = await SeedPublishedScenarioAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var attemptId = await CreateDraftAttemptAsync(client, scenario.Id);
        var key = $"submit-{Guid.NewGuid():N}";

        using var req1 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
        {
            Content = JsonContent.Create(new { answer = "Initial Answer A" })
        };
        req1.Headers.Add("Idempotency-Key", key);
        using var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Accepted, res1.StatusCode);

        using var req2 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
        {
            Content = JsonContent.Create(new { answer = "Initial Answer A" })
        };
        req2.Headers.Add("Idempotency-Key", key);
        using var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.Accepted, res2.StatusCode);

        using var req3 = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
        {
            Content = JsonContent.Create(new { answer = "Different Answer B" })
        };
        req3.Headers.Add("Idempotency-Key", key);
        using var res3 = await client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.Conflict, res3.StatusCode);
    }

    [Fact]
    public async Task ScenarioConcurrentSubmitOnlyOneWinsAndNoDoubleQuotaReservation()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.Scenario, 2);
        var scenario = await SeedPublishedScenarioAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var attemptId = await CreateDraftAttemptAsync(client, scenario.Id);

        using var client1 = factory.CreateHttpsClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var client2 = factory.CreateHttpsClient();
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var task1 = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
            {
                Content = JsonContent.Create(new { answer = "Concurrent answer 1" })
            };
            req.Headers.Add("Idempotency-Key", $"key-concurrent-{Guid.NewGuid():N}");
            return await client1.SendAsync(req);
        });

        var task2 = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
            {
                Content = JsonContent.Create(new { answer = "Concurrent answer 2" })
            };
            req.Headers.Add("Idempotency-Key", $"key-concurrent-{Guid.NewGuid():N}");
            return await client2.SendAsync(req);
        });

        var responses = await Task.WhenAll(task1, task2);
        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.Accepted);
        var conflictCount = responses.Count(r => r.StatusCode == HttpStatusCode.Conflict);

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var outboxCount = await db.OutboxEvents.CountAsync(item => item.AggregateId == attemptId);
        Assert.Equal(1, outboxCount);

        var ef = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId && item.FeatureCode == FeatureValues.Scenario);
        Assert.Equal(1, ef.Reserved);
    }

    [Fact]
    public async Task StarConcurrentSameKeyCreatesOneAttemptAndOneReservation()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.StarBuilder, 5);

        var key = $"star-concurrent-{Guid.NewGuid():N}";
        using var client1 = factory.CreateHttpsClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var client2 = factory.CreateHttpsClient();
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var task1 = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-attempts")
            {
                Content = JsonContent.Create(new { question = "STAR question", answer = "STAR answer" })
            };
            req.Headers.Add("Idempotency-Key", key);
            return await client1.SendAsync(req);
        });

        var task2 = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-attempts")
            {
                Content = JsonContent.Create(new { question = "STAR question", answer = "STAR answer" })
            };
            req.Headers.Add("Idempotency-Key", key);
            return await client2.SendAsync(req);
        });

        var responses = await Task.WhenAll(task1, task2);
        Assert.Equal(HttpStatusCode.Created, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.Created, responses[1].StatusCode);

        using var doc1 = JsonDocument.Parse(await responses[0].Content.ReadAsStringAsync());
        using var doc2 = JsonDocument.Parse(await responses[1].Content.ReadAsStringAsync());
        var id1 = doc1.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var id2 = doc2.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        Assert.Equal(id1, id2);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var totalAttempts = await db.StarAttempts.CountAsync(item => item.UserId == account.UserId);
        Assert.Equal(1, totalAttempts);

        var ef = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId && item.FeatureCode == FeatureValues.StarBuilder);
        Assert.Equal(1, ef.Reserved);
    }

    [Fact]
    public async Task CvAnalysisConcurrentSameKeyCreatesOneAnalysisAndOneReservation()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.CvAnalysis, 5);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var storedFile = new Nexora.Data.Practice.StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            StorageKey = "files/test.pdf",
            FileName = "test.pdf",
            ContentType = "application/pdf",
            Size = 100,
            Checksum = "hash",
            CreatedAt = now
        };
        var resume = new Nexora.Data.Practice.ResumeRecord
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            StoredFileId = storedFile.Id,
            StoredFile = storedFile,
            Status = PracticeValues.Ready,
            ExtractedText = "Experienced C# engineer with ASP.NET Core and PostgreSQL skills.",
            StructuredProfile = "{\"summary\":\"Experienced C# engineer\",\"skills\":[\"C#\",\"PostgreSQL\"],\"experiences\":[],\"education\":[],\"projects\":[],\"certifications\":[],\"languages\":[]}",
            ProfileModelVersion = "test-gemini-model",
            ProfilePromptVersion = "resume-profile-v2",
            ProfileSchemaVersion = "resume-profile-v2",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var jd = new Nexora.Data.Practice.JobDescription
        {
            Id = Guid.NewGuid(),
            UserId = account.UserId,
            Title = "Backend Dev",
            Content = "Requirements: C#, ASP.NET Core, PostgreSQL",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(storedFile, resume, jd);
        await db.SaveChangesAsync();

        var key = $"cv-concurrent-{Guid.NewGuid():N}";
        using var client1 = factory.CreateHttpsClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var client2 = factory.CreateHttpsClient();
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var task1 = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
            {
                Content = JsonContent.Create(new { resumeId = resume.Id, mode = "job_targeted", jobDescriptionId = jd.Id })
            };
            req.Headers.Add("Idempotency-Key", key);
            return await client1.SendAsync(req);
        });

        var task2 = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
            {
                Content = JsonContent.Create(new { resumeId = resume.Id, mode = "job_targeted", jobDescriptionId = jd.Id })
            };
            req.Headers.Add("Idempotency-Key", key);
            return await client2.SendAsync(req);
        });

        var responses = await Task.WhenAll(task1, task2);
        Assert.Equal(HttpStatusCode.Created, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.Created, responses[1].StatusCode);

        using var doc1 = JsonDocument.Parse(await responses[0].Content.ReadAsStringAsync());
        using var doc2 = JsonDocument.Parse(await responses[1].Content.ReadAsStringAsync());
        var id1 = doc1.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        var id2 = doc2.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        Assert.Equal(id1, id2);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var totalAnalyses = await db2.ResumeAnalyses.CountAsync(item => item.UserId == account.UserId);
        Assert.Equal(1, totalAnalyses);

        var ef = await db2.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(
            item => item.Entitlement.UserId == account.UserId && item.Entitlement.PlanCodeSnapshot != "free" && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(1, ef.Reserved);
    }

    [Fact]
    public async Task CheckoutConcurrentSameKeyRecoversWinnerWithoutRollbackCommit()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var price = await db.PlanPrices.FirstAsync(p => p.IsActive && p.AmountMinor > 0);

        var key = $"checkout-concurrent-{Guid.NewGuid():N}";
        using var client1 = factory.CreateHttpsClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var client2 = factory.CreateHttpsClient();
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var task1 = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions")
            {
                Content = JsonContent.Create(new { planPriceId = price.Id })
            };
            req.Headers.Add("Idempotency-Key", key);
            return await client1.SendAsync(req);
        });

        var task2 = Task.Run(async () =>
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions")
            {
                Content = JsonContent.Create(new { planPriceId = price.Id })
            };
            req.Headers.Add("Idempotency-Key", key);
            return await client2.SendAsync(req);
        });

        var responses = await Task.WhenAll(task1, task2);
        Assert.Equal(HttpStatusCode.Created, responses[0].StatusCode);
        Assert.Equal(HttpStatusCode.Created, responses[1].StatusCode);

        using var doc1 = JsonDocument.Parse(await responses[0].Content.ReadAsStringAsync());
        using var doc2 = JsonDocument.Parse(await responses[1].Content.ReadAsStringAsync());
        var orderId1 = doc1.RootElement.GetProperty("data").GetProperty("orderId").GetGuid();
        var orderId2 = doc2.RootElement.GetProperty("data").GetProperty("orderId").GetGuid();
        Assert.Equal(orderId1, orderId2);

        using var scope2 = factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var orderCount = await db2.Orders.CountAsync(item => item.UserId == account.UserId);
        Assert.Equal(1, orderCount);
    }

    private static async Task<Guid> CreateDraftAttemptAsync(HttpClient client, Guid scenarioId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId })
        };
        request.Headers.Add("Idempotency-Key", $"draft-{Guid.NewGuid():N}");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("id").GetGuid();
    }

    private static async Task<Grant> GrantPlanAsync(NexoraApiFactory factory, Guid userId, Guid? planPriceId = null)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var targetPriceId = planPriceId ?? (await db.PlanPrices.Include(item => item.Features).FirstAsync()).Id;
            using var client = factory.CreateHttpsClient();
            var admin = await MakeAdminAsync(factory, userId);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", admin.AccessToken);
            using (var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/users/{userId}/plan-grants")
            {
                Content = JsonContent.Create(new { planPriceId = targetPriceId, replaceCurrent = true, reason = "Test grant" })
            })
            {
                request.Headers.Add("Idempotency-Key", $"grant-{Guid.NewGuid():N}");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                return new Grant(json.RootElement.GetProperty("data").GetProperty("entitlementId").GetGuid());
            }
        }
    }

    private static async Task<ResumeFixture> SeedReadyResumeAsync(NexoraApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var storedFile = new Nexora.Data.Practice.StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = $"files/free-cv-{Guid.NewGuid():N}.pdf",
            FileName = "cv.pdf",
            ContentType = "application/pdf",
            Size = 1024,
            Checksum = "free-cv-fixture",
            CreatedAt = now
        };
        var resume = new Nexora.Data.Practice.ResumeRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StoredFileId = storedFile.Id,
            StoredFile = storedFile,
            Status = PracticeValues.Ready,
            ExtractedText = "Experienced C# engineer with ASP.NET Core and PostgreSQL skills.",
            StructuredProfile = "{\"summary\":\"Experienced C# engineer\",\"skills\":[\"C#\",\"PostgreSQL\"],\"experiences\":[],\"education\":[],\"projects\":[],\"certifications\":[],\"languages\":[]}",
            ProfileModelVersion = "test-gemini-model",
            ProfilePromptVersion = "resume-profile-v2",
            ProfileSchemaVersion = "resume-profile-v2",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        var jobDescription = new Nexora.Data.Practice.JobDescription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "Backend Engineer",
            Content = "Requirements: C# and PostgreSQL",
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(storedFile, resume, jobDescription);
        await db.SaveChangesAsync();
        return new ResumeFixture(resume.Id, jobDescription.Id);
    }

    private static object CreateAnalysisPayload(ResumeFixture fixture, string mode) =>
        mode == ResumeAnalysisModes.JobTargeted
            ? new { resumeId = fixture.ResumeId, mode, jobDescriptionId = fixture.JobDescriptionId }
            : new { resumeId = fixture.ResumeId, mode, industry = "Fintech", targetRole = "Backend Engineer", seniority = "senior" };

    private static async Task<HttpResponseMessage> SendResumeAnalysisAsync(HttpClient client, ResumeFixture fixture, string mode, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(CreateAnalysisPayload(fixture, mode))
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<Nexora.Data.Practice.Scenario> SeedPublishedScenarioAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var category = await db.ScenarioCategories.FirstAsync();
        var scenario = new Nexora.Data.Practice.Scenario
        {
            Id = Guid.NewGuid(),
            Slug = $"scenario-{Guid.NewGuid():N}",
            Title = "Scenario test",
            Summary = "Published scenario for tests",
            CategoryId = category.Id,
            Difficulty = "medium",
            Competency = "problem_analysis",
            EstimatedMinutes = 15,
            Content = "A rich scenario body describing a business problem to solve.",
            SortOrder = 1,
            Status = "published",
            CreatedAt = now,
            UpdatedAt = now,
            PublishedAt = now
        };
        db.Scenarios.Add(scenario);
        await db.SaveChangesAsync();
        return scenario;
    }

    private static async Task SeedFeatureEntitlementAsync(NexoraApiFactory factory, Guid userId, string featureCode, int limit)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"feature-{Guid.NewGuid():N}", Name = "Feature test", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 1, Currency = "VND", DurationDays = 30, InterviewQuota = 1, IsActive = true, CreatedAt = now };
        var subscription = new Subscription { Id = Guid.NewGuid(), UserId = userId, Status = BillingValues.Active, StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SubscriptionId = subscription.Id,
            PlanCodeSnapshot = plan.Code,
            Status = BillingValues.Active,
            InterviewLimit = 1,
            StartsAt = subscription.StartsAt,
            EndsAt = subscription.EndsAt,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        var featureDef = await db.FeatureDefinitions.SingleAsync(item => item.Code == featureCode);
        var ef = new EntitlementFeature
        {
            Id = Guid.NewGuid(),
            EntitlementId = entitlement.Id,
            FeatureDefinitionId = featureDef.Id,
            FeatureCode = featureCode,
            IsEnabled = true,
            Limit = limit,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        db.AddRange(plan, price, subscription, entitlement, ef);
        await db.SaveChangesAsync();
    }

    private static async Task<AdminAccount> MakeAdminAsync(NexoraApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await roleManager.RoleExistsAsync("Admin") is false) await roleManager.CreateAsync(new IdentityRole<Guid>("Admin"));
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is not null && await userManager.IsInRoleAsync(user, "Admin") is false) await userManager.AddToRoleAsync(user, "Admin");

        // Re-issue a token that carries the role claim via login.
        using var client = factory.CreateHttpsClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = user?.Email, password = "Strong!Pass123" });
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return new AdminAccount(json.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
    }

    private static async Task ProcessJobsAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var practice = scope.ServiceProvider.GetRequiredService<Nexora.Business.Practice.IPracticeJobProcessor>();
        var scenarioStar = scope.ServiceProvider.GetRequiredService<Nexora.Business.Practice.IScenarioStarJobProcessor>();
        await practice.ProcessPendingAsync(CancellationToken.None);
        await scenarioStar.ProcessPendingAsync(CancellationToken.None);
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"platform-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Platform candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private sealed record Account(Guid UserId, string AccessToken);
    private sealed record AdminAccount(string AccessToken);
    private sealed record Grant(Guid EntitlementId);
    private sealed record ResumeFixture(Guid ResumeId, Guid JobDescriptionId);

    private sealed class FailingAiProvider : Nexora.Business.Ai.IAiProvider
    {
        public string ModelVersion => "test-gemini-model";
        public Task<T> GenerateStructuredAsync<T>(Nexora.Business.Ai.AiRequest request, CancellationToken cancellationToken) =>
            Task.FromException<T>(new TimeoutException("Deterministic failure"));
    }
}
