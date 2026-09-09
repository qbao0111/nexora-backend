using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class ScenarioV2ApiTests
{
    [Fact]
    public async Task ScenarioCatalogueExposesTracksAndGroupsByCategoryAndDifficulty()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        await SeedScenarioAsync(factory, "banking", "hard", "risk_management");
        await SeedScenarioAsync(factory, "ecommerce", "easy", "customer_focus");

        using var categoriesResponse = await client.GetAsync("/api/v1/scenarios/categories");
        Assert.Equal(HttpStatusCode.OK, categoriesResponse.StatusCode);
        var categories = await DataAsync(categoriesResponse);
        Assert.Contains(categories.EnumerateArray(), item => item.GetProperty("slug").GetString() == "banking");
        Assert.Contains(categories.EnumerateArray(), item => item.GetProperty("slug").GetString() == "ecommerce");

        using var listResponse = await client.GetAsync("/api/v1/scenarios?category=banking&difficulty=hard");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var page = await DataAsync(listResponse);
        Assert.True(page.GetProperty("total").GetInt32() >= 1);
        Assert.All(page.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.Equal("banking", item.GetProperty("categorySlug").GetString());
            Assert.Equal("hard", item.GetProperty("difficulty").GetString());
        });
    }

    [Fact]
    public async Task ScenarioRetryCreatesNewAttemptAndHistoryComparesCompletedScores()
    {
        var ai = new TestAiProvider();
        ai.EnqueueResponse(AiPurposes.ScenarioEvaluate, Evaluation(70));
        ai.EnqueueResponse(AiPurposes.ScenarioEvaluate, Evaluation(90));
        using var factory = new NexoraApiFactory(ai);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenario = await SeedScenarioAsync(factory, "banking", "medium", "risk_management");
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.Scenario, 2);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var firstAttemptId = await SubmitAsync(client, factory, scenario.Id, "first-answer", "first-submit");

        using var retryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenarios/{scenario.Id}/retry");
        retryRequest.Headers.Add("Idempotency-Key", "scenario-retry");
        using var retryResponse = await client.SendAsync(retryRequest);
        var retryBody = await retryResponse.Content.ReadAsStringAsync();
        Assert.True(retryResponse.StatusCode == HttpStatusCode.Created, retryBody);
        var retry = await DataAsync(retryResponse);
        var secondAttemptId = retry.GetProperty("id").GetGuid();
        Assert.NotEqual(firstAttemptId, secondAttemptId);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenarios/{scenario.Id}/retry");
        replayRequest.Headers.Add("Idempotency-Key", "scenario-retry");
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Equal(secondAttemptId, (await DataAsync(replayResponse)).GetProperty("id").GetGuid());

        using var submitRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{secondAttemptId}/submit")
        {
            Content = JsonContent.Create(new { answer = "second-answer" })
        };
        submitRequest.Headers.Add("Idempotency-Key", "second-submit");
        using var submitResponse = await client.SendAsync(submitRequest);
        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        await ProcessScenarioJobsAsync(factory);

        using var historyResponse = await client.GetAsync($"/api/v1/scenarios/{scenario.Id}/attempts");
        Assert.Equal(HttpStatusCode.OK, historyResponse.StatusCode);
        var history = await DataAsync(historyResponse);
        var attempts = history.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Equal(2, attempts.Length);
        Assert.Equal(90, attempts[0].GetProperty("overallScore").GetInt32());
        Assert.Equal(70, attempts[0].GetProperty("previousScore").GetInt32());
        Assert.Equal(20, attempts[0].GetProperty("scoreDelta").GetInt32());
        Assert.True(attempts[0].GetProperty("improved").GetBoolean());
        Assert.Equal(70, attempts[1].GetProperty("overallScore").GetInt32());
        Assert.True(attempts[1].GetProperty("previousScore").ValueKind == JsonValueKind.Null);
        var comparison = history.GetProperty("comparison");
        Assert.Equal(90, comparison.GetProperty("currentScore").GetInt32());
        Assert.Equal(70, comparison.GetProperty("previousScore").GetInt32());
        Assert.Equal(20, comparison.GetProperty("delta").GetInt32());
        Assert.True(comparison.GetProperty("improved").GetBoolean());
        Assert.Equal(90, history.GetProperty("latestScore").GetInt32());
        Assert.Equal(90, history.GetProperty("bestScore").GetInt32());
    }

    [Fact]
    public async Task ScenarioHistoryIsOwnerScopedAndSingleAttemptHasNoPreviousComparison()
    {
        var ai = new TestAiProvider();
        ai.EnqueueResponse(AiPurposes.ScenarioEvaluate, Evaluation(75));
        ai.EnqueueResponse(AiPurposes.ScenarioEvaluate, Evaluation(95));
        using var factory = new NexoraApiFactory(ai);
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        var scenario = await SeedScenarioAsync(factory, "banking", "medium", "risk_management");
        await SeedFeatureEntitlementAsync(factory, owner.UserId, FeatureValues.Scenario, 1);
        await SeedFeatureEntitlementAsync(factory, other.UserId, FeatureValues.Scenario, 1);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);

        var ownerAttemptId = await SubmitAsync(ownerClient, factory, scenario.Id, "owner-answer", "owner-submit");
        var otherAttemptId = await SubmitAsync(otherClient, factory, scenario.Id, "other-answer", "other-submit");
        var unrelatedScenario = await SeedScenarioAsync(factory, "ecommerce", "easy", "customer_focus");
        await SeedCompletedAttemptAsync(factory, owner.UserId, unrelatedScenario.Id, 99);

        using var ownerHistory = await ownerClient.GetAsync($"/api/v1/scenarios/{scenario.Id}/attempts");
        Assert.Equal(HttpStatusCode.OK, ownerHistory.StatusCode);
        var history = await DataAsync(ownerHistory);
        var attempts = history.GetProperty("attempts").EnumerateArray().ToArray();
        Assert.Single(attempts);
        Assert.Equal(ownerAttemptId, attempts[0].GetProperty("id").GetGuid());
        Assert.Equal(75, history.GetProperty("comparison").GetProperty("currentScore").GetInt32());
        Assert.True(history.GetProperty("comparison").GetProperty("previousScore").ValueKind == JsonValueKind.Null);
        Assert.True(history.GetProperty("comparison").GetProperty("delta").ValueKind == JsonValueKind.Null);
        Assert.True(history.GetProperty("comparison").GetProperty("improved").ValueKind == JsonValueKind.Null);

        using var otherAttempt = await ownerClient.GetAsync($"/api/v1/scenario-attempts/{otherAttemptId}");
        Assert.Equal(HttpStatusCode.NotFound, otherAttempt.StatusCode);
    }

    [Fact]
    public async Task ScenarioRetryRejectsAnUnfinishedLatestAttempt()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenario = await SeedScenarioAsync(factory, "banking", "easy", "risk_management");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var missingRetry = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenarios/{Guid.NewGuid()}/retry");
        missingRetry.Headers.Add("Idempotency-Key", "missing-scenario-retry");
        using var missingRetryResponse = await client.SendAsync(missingRetry);
        Assert.Equal(HttpStatusCode.NotFound, missingRetryResponse.StatusCode);

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId = scenario.Id })
        };
        create.Headers.Add("Idempotency-Key", "unfinished-draft");
        using var createResponse = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);

        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenarios/{scenario.Id}/retry");
        retry.Headers.Add("Idempotency-Key", "unfinished-retry");
        using var retryResponse = await client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.Conflict, retryResponse.StatusCode);
        var error = await retryResponse.Content.ReadAsStringAsync();
        Assert.Contains("SCENARIO_ATTEMPT_IN_PROGRESS", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScenarioProgressAggregatesByTrackAndCompetencyAndAdvancesDifficulty()
    {
        var ai = new TestAiProvider();
        ai.EnqueueResponse(AiPurposes.ScenarioEvaluate, Evaluation(90));
        using var factory = new NexoraApiFactory(ai);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var scenario = await SeedScenarioAsync(factory, "logistics", "medium", "stakeholder_management");
        await SeedFeatureEntitlementAsync(factory, account.UserId, FeatureValues.Scenario, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        await SubmitAsync(client, factory, scenario.Id, "progress-answer", "progress-submit");
        await SeedFailedAttemptAsync(factory, account.UserId, scenario.Id);

        using var response = await client.GetAsync("/api/v1/scenarios/progress");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var progress = await DataAsync(response);
        Assert.Equal("hard", progress.GetProperty("recommendedDifficulty").GetString());
        Assert.Equal(2, progress.GetProperty("attemptCount").GetInt32());
        Assert.Equal(1, progress.GetProperty("completedAttempts").GetInt32());
        Assert.Equal(90, progress.GetProperty("averageScore").GetDouble());

        var track = Assert.Single(progress.GetProperty("tracks").EnumerateArray(), item => item.GetProperty("categorySlug").GetString() == "logistics");
        Assert.Equal(1, track.GetProperty("completedAttempts").GetInt32());
        Assert.Equal(90, track.GetProperty("averageScore").GetDouble());

        var competency = Assert.Single(progress.GetProperty("competencies").EnumerateArray(), item => item.GetProperty("competency").GetString() == "stakeholder_management");
        Assert.Equal(90, competency.GetProperty("bestScore").GetInt32());
        Assert.Equal(90, competency.GetProperty("latestScore").GetInt32());

        var difficulty = Assert.Single(progress.GetProperty("difficulties").EnumerateArray(), item => item.GetProperty("difficulty").GetString() == "medium");
        Assert.Equal(90, difficulty.GetProperty("averageScore").GetDouble());
    }

    [Fact]
    public async Task ScenarioProgressRecommendedDifficultyAndScoreBoundsEnforced()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        // 1. No completed attempts -> recommended is "easy"
        using var initialResp = await client.GetAsync("/api/v1/scenarios/progress");
        Assert.Equal(HttpStatusCode.OK, initialResp.StatusCode);
        var initialProgress = await DataAsync(initialResp);
        Assert.Equal("easy", initialProgress.GetProperty("recommendedDifficulty").GetString());
        Assert.Equal(0, initialProgress.GetProperty("completedAttempts").GetInt32());

        // 2. Score < 80 on medium -> keeps current difficulty ("medium")
        var mediumScenario = await SeedScenarioAsync(factory, "banking", "medium", "risk_management");
        await SeedCompletedAttemptAsync(factory, account.UserId, mediumScenario.Id, 75);

        using var medResp = await client.GetAsync("/api/v1/scenarios/progress");
        var medProgress = await DataAsync(medResp);
        Assert.Equal("medium", medProgress.GetProperty("recommendedDifficulty").GetString());

        // 3. Score >= 80 on easy -> advances to "medium"
        var easyScenario = await SeedScenarioAsync(factory, "ecommerce", "easy", "customer_focus");
        await SeedCompletedAttemptAsync(factory, account.UserId, easyScenario.Id, 85);

        using var easyResp = await client.GetAsync("/api/v1/scenarios/progress");
        var easyProgress = await DataAsync(easyResp);
        Assert.Equal("medium", easyProgress.GetProperty("recommendedDifficulty").GetString());

        // 4. Hard never advances beyond hard
        var hardScenario = await SeedScenarioAsync(factory, "logistics", "hard", "incident_response");
        await SeedCompletedAttemptAsync(factory, account.UserId, hardScenario.Id, 95);

        using var hardResp = await client.GetAsync("/api/v1/scenarios/progress");
        var hardProgress = await DataAsync(hardResp);
        Assert.Equal("hard", hardProgress.GetProperty("recommendedDifficulty").GetString());

        // 5. Invalid/out-of-range evaluation scores (>100, <0, non-numeric) are ignored
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow.AddSeconds(10);
        db.ScenarioAttempts.AddRange(
            new ScenarioAttempt
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                ScenarioId = hardScenario.Id,
                Status = PracticeFeatureValues.Completed,
                Answer = "out-of-range-high",
                EvaluationJson = "{\"overallScore\":150}",
                ModelVersion = "test-model",
                PromptVersion = "test-prompt",
                SchemaVersion = "test-schema",
                CreatedAt = now,
                UpdatedAt = now,
                CompletedAt = now
            },
            new ScenarioAttempt
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                ScenarioId = hardScenario.Id,
                Status = PracticeFeatureValues.Completed,
                Answer = "out-of-range-low",
                EvaluationJson = "{\"overallScore\":-5}",
                ModelVersion = "test-model",
                PromptVersion = "test-prompt",
                SchemaVersion = "test-schema",
                CreatedAt = now.AddSeconds(1),
                UpdatedAt = now.AddSeconds(1),
                CompletedAt = now.AddSeconds(1)
            },
            new ScenarioAttempt
            {
                Id = Guid.NewGuid(),
                UserId = account.UserId,
                ScenarioId = hardScenario.Id,
                Status = PracticeFeatureValues.Completed,
                Answer = "non-numeric",
                EvaluationJson = "{\"overallScore\":\"invalid\"}",
                ModelVersion = "test-model",
                PromptVersion = "test-prompt",
                SchemaVersion = "test-schema",
                CreatedAt = now.AddSeconds(2),
                UpdatedAt = now.AddSeconds(2),
                CompletedAt = now.AddSeconds(2)
            }
        );
        await db.SaveChangesAsync();

        using var boundsResp = await client.GetAsync("/api/v1/scenarios/progress");
        var boundsProgress = await DataAsync(boundsResp);
        // Valid completed attempts remain 3 (75, 85, 95)
        Assert.Equal(3, boundsProgress.GetProperty("completedAttempts").GetInt32());
        // Average is (75 + 85 + 95) / 3 = 85.0
        Assert.Equal(85.0, boundsProgress.GetProperty("averageScore").GetDouble());
    }

    private static async Task SeedFailedAttemptAsync(NexoraApiFactory factory, Guid userId, Guid scenarioId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        db.ScenarioAttempts.Add(new ScenarioAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ScenarioId = scenarioId,
            Status = PracticeFeatureValues.Failed,
            Answer = "failed-answer",
            EvaluationJson = "{\"overallScore\":100}",
            ErrorCode = "AI_PROCESSING_FAILED",
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedCompletedAttemptAsync(NexoraApiFactory factory, Guid userId, Guid scenarioId, int score)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        db.ScenarioAttempts.Add(new ScenarioAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ScenarioId = scenarioId,
            Status = PracticeFeatureValues.Completed,
            Answer = "unrelated-answer",
            EvaluationJson = $"{{\"overallScore\":{score}}}",
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SubmitAsync(HttpClient client, NexoraApiFactory factory, Guid scenarioId, string answer, string key)
    {
        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/scenario-attempts")
        {
            Content = JsonContent.Create(new { scenarioId })
        };
        create.Headers.Add("Idempotency-Key", $"draft-{key}");
        using var createResponse = await client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var draft = await DataAsync(createResponse);
        var attemptId = draft.GetProperty("id").GetGuid();

        using var submit = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/scenario-attempts/{attemptId}/submit")
        {
            Content = JsonContent.Create(new { answer })
        };
        submit.Headers.Add("Idempotency-Key", key);
        using var submitResponse = await client.SendAsync(submit);
        Assert.Equal(HttpStatusCode.Accepted, submitResponse.StatusCode);
        await ProcessScenarioJobsAsync(factory);
        return attemptId;
    }

    private static async Task<Scenario> SeedScenarioAsync(NexoraApiFactory factory, string categorySlug, string difficulty, string competency)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var category = await db.ScenarioCategories.SingleAsync(item => item.Slug == categorySlug);
        var scenario = new Scenario
        {
            Id = Guid.NewGuid(),
            Slug = $"scenario-v2-{Guid.NewGuid():N}",
            Title = $"Scenario v2 {categorySlug}",
            Summary = "Scenario v2 integration test scenario",
            CategoryId = category.Id,
            Difficulty = difficulty,
            Competency = competency,
            EstimatedMinutes = 15,
            Content = "A deterministic scenario body for Scenario v2 integration tests.",
            SortOrder = 1,
            Status = PracticeFeatureValues.Published,
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
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"scenario-v2-{Guid.NewGuid():N}", Name = "Scenario v2 test", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 1, Currency = "VND", DurationDays = 30, InterviewQuota = 1, IsActive = true, CreatedAt = now };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = BillingValues.Active,
            StartsAt = now.AddMinutes(-1),
            EndsAt = now.AddDays(30),
            CreatedAt = now,
            UpdatedAt = now
        };
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
        var definition = await db.FeatureDefinitions.SingleAsync(item => item.Code == featureCode);
        var feature = new EntitlementFeature
        {
            Id = Guid.NewGuid(),
            EntitlementId = entitlement.Id,
            FeatureDefinitionId = definition.Id,
            FeatureCode = featureCode,
            IsEnabled = true,
            Limit = limit,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        db.AddRange(plan, price, subscription, entitlement, feature);
        await db.SaveChangesAsync();
    }

    private static ScenarioEvaluationResult Evaluation(int score) => new(
        score,
        [
            new ScenarioDimensionEvaluation("problem_analysis", score, "The answer identifies the problem and evidence.", "Keep the analysis structured."),
            new ScenarioDimensionEvaluation("communication", score, "The answer explains the approach clearly.", "Make the communication more concise.")
        ],
        ["Clear analysis"],
        ["Add a fallback plan"],
        ["State the expected impact"],
        "Structured scenario feedback.",
        AiOperations.ScoreScale);

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"scenario-v2-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Scenario v2 candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task ProcessScenarioJobsAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IScenarioStarJobProcessor>().ProcessPendingAsync(CancellationToken.None);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
