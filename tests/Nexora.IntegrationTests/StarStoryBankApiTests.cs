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
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class StarStoryBankApiTests
{
    private static readonly string[] SaveTags = [" Leadership ", "leadership", "Ownership"];
    private static readonly string[] EmptyTags = [];
    private static readonly string[] EditedTags = [" Leadership ", "leadership", "Problem-Solving"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task SaveFromOwnedAttemptPreservesEvidenceAndIsIdempotent()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        var attemptId = await SeedCompletedAttemptAsync(factory, owner.UserId, "checkout-recovery");
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);

        using var create = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-stories")
        {
            Content = JsonContent.Create(new
            {
                sourceAttemptId = attemptId,
                title = "  Checkout recovery  ",
                tags = SaveTags
            })
        };
        create.Headers.Add("Idempotency-Key", "story-save-1");
        using var createdResponse = await ownerClient.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var created = await DataAsync(createdResponse);
        var storyId = created.GetProperty("id").GetGuid();
        Assert.Equal("Checkout recovery", created.GetProperty("title").GetString());
        Assert.Equal(["leadership", "ownership"], created.GetProperty("tags").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal("payment outage happened", created.GetProperty("situation").GetString());
        Assert.Equal("restore checkout", created.GetProperty("task").GetString());
        Assert.Equal("checked logs and added a retry", created.GetProperty("action").GetString());
        Assert.Equal("checkout recovered in ten minutes", created.GetProperty("result").GetString());
        Assert.Equal(84, created.GetProperty("latestScore").GetInt32());

        using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-stories")
        {
            Content = JsonContent.Create(new { sourceAttemptId = attemptId, title = "  Checkout recovery  ", tags = SaveTags })
        };
        replay.Headers.Add("Idempotency-Key", "story-save-1");
        using var replayResponse = await ownerClient.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Equal(storyId, (await DataAsync(replayResponse)).GetProperty("id").GetGuid());

        using var conflict = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-stories")
        {
            Content = JsonContent.Create(new { sourceAttemptId = attemptId, title = "Different title", tags = EmptyTags })
        };
        conflict.Headers.Add("Idempotency-Key", "story-save-1");
        using var conflictResponse = await ownerClient.SendAsync(conflict);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

        using var foreignSave = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-stories")
        {
            Content = JsonContent.Create(new { sourceAttemptId = attemptId, title = "Leaked", tags = EmptyTags })
        };
        foreignSave.Headers.Add("Idempotency-Key", "foreign-save");
        using var foreignSaveResponse = await otherClient.SendAsync(foreignSave);
        Assert.Equal(HttpStatusCode.NotFound, foreignSaveResponse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/star-stories/{storyId}")).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.StarStories.CountAsync(item => item.UserId == owner.UserId));
        var source = await db.StarAttempts.SingleAsync(item => item.Id == attemptId);
        Assert.Equal(PracticeFeatureValues.Completed, source.Status);
        Assert.Contains("payment outage happened", source.Answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OwnerCanEditAndSearchStoryButOtherUserCannotAccessIt()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        var firstAttempt = await SeedCompletedAttemptAsync(factory, owner.UserId, "first-story");
        var secondAttempt = await SeedCompletedAttemptAsync(factory, owner.UserId, "second-story");
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        var firstStoryId = await SaveAsync(ownerClient, firstAttempt, "First story", ["leadership"], "save-first");
        var secondStoryId = await SaveAsync(ownerClient, secondAttempt, "Second story", ["conflict"], "save-second");

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/star-stories/{firstStoryId}")
        {
            Content = JsonContent.Create(new
            {
                title = "Edited ownership story",
                tags = EditedTags,
                situation = "edited situation",
                task = "edited task",
                action = "edited action",
                result = "edited result"
            })
        };
        using var patchedResponse = await ownerClient.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patchedResponse.StatusCode);
        var patched = await DataAsync(patchedResponse);
        Assert.Equal("Edited ownership story", patched.GetProperty("title").GetString());
        Assert.Equal(["leadership", "problem-solving"], patched.GetProperty("tags").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal("edited action", patched.GetProperty("action").GetString());
        Assert.Equal(84, patched.GetProperty("latestScore").GetInt32());
        Assert.True(patched.GetProperty("updatedAt").GetDateTimeOffset() >= patched.GetProperty("createdAt").GetDateTimeOffset());

        using var listResponse = await ownerClient.GetAsync("/api/v1/star-stories?search=ownership&pageSize=1");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var list = await DataAsync(listResponse);
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(firstStoryId, list.GetProperty("items")[0].GetProperty("id").GetGuid());

        using var tagResponse = await ownerClient.GetAsync("/api/v1/star-stories?tag=problem-solving");
        Assert.Equal(1, (await DataAsync(tagResponse)).GetProperty("total").GetInt32());
        using var conflictTagResponse = await ownerClient.GetAsync("/api/v1/star-stories?tag=conflict");
        Assert.Equal(secondStoryId, (await DataAsync(conflictTagResponse)).GetProperty("items")[0].GetProperty("id").GetGuid());

        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/star-stories/{firstStoryId}")).StatusCode);
        using var foreignPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/star-stories/{firstStoryId}")
        {
            Content = JsonContent.Create(new { title = "No access", tags = EmptyTags, situation = "s", task = "t", action = "a", result = "r" })
        };
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.SendAsync(foreignPatch)).StatusCode);

        using var blankPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/star-stories/{firstStoryId}")
        {
            Content = JsonContent.Create(new { title = "", tags = EmptyTags, situation = "s", task = "t", action = "a", result = "r" })
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.SendAsync(blankPatch)).StatusCode);
        using var oversizedPatch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/star-stories/{firstStoryId}")
        {
            Content = JsonContent.Create(new { title = "Too long", tags = EmptyTags, situation = new string('x', 8_001), task = "t", action = "a", result = "r" })
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await ownerClient.SendAsync(oversizedPatch)).StatusCode);
    }

    [Fact]
    public async Task EvaluateUsesCurrentStoryContentIsIdempotentAndPreservesScoreOnFailure()
    {
        var ai = new TestAiProvider();
        ai.EnqueueResponse(AiPurposes.StarEvaluate, ValidEvaluation(95));
        ai.EnqueueResponse(AiPurposes.StarEvaluate, new AiProviderException(AiProviderFailureKind.Unavailable, "temporary test failure"));
        using var factory = new NexoraApiFactory(ai);
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        await SeedFeatureEntitlementAsync(factory, owner.UserId, FeatureValues.StarBuilder, 5);
        var attemptId = await SeedCompletedAttemptAsync(factory, owner.UserId, "evaluate-story");
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        var storyId = await SaveAsync(ownerClient, attemptId, "Evaluate me", [], "save-evaluate");

        using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/star-stories/{storyId}")
        {
            Content = JsonContent.Create(new { title = "Evaluate me", tags = EmptyTags, situation = "edited current situation", task = "edited current task", action = "edited current action", result = "edited current result" })
        };
        Assert.Equal(HttpStatusCode.OK, (await ownerClient.SendAsync(patch)).StatusCode);

        using var evaluate = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/star-stories/{storyId}/evaluate");
        evaluate.Headers.Add("Idempotency-Key", "story-evaluate-1");
        using var evaluatedResponse = await ownerClient.SendAsync(evaluate);
        Assert.Equal(HttpStatusCode.OK, evaluatedResponse.StatusCode);
        var evaluated = await DataAsync(evaluatedResponse);
        Assert.Equal(95, evaluated.GetProperty("latestScore").GetInt32());
        var invocation = Assert.Single(ai.Invocations, item => item.Purpose == AiPurposes.StarEvaluate);
        Assert.Contains("edited current situation", invocation.UntrustedInput, StringComparison.Ordinal);
        Assert.DoesNotContain("payment outage happened", invocation.UntrustedInput, StringComparison.Ordinal);

        using var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/star-stories/{storyId}/evaluate");
        replay.Headers.Add("Idempotency-Key", "story-evaluate-1");
        using var replayResponse = await ownerClient.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(95, (await DataAsync(replayResponse)).GetProperty("latestScore").GetInt32());
        Assert.Equal(1, ai.GetCallCount(AiPurposes.StarEvaluate));

        using var failure = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/star-stories/{storyId}/evaluate");
        failure.Headers.Add("Idempotency-Key", "story-evaluate-failure");
        using var failureResponse = await ownerClient.SendAsync(failure);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failureResponse.StatusCode);
        using var currentResponse = await ownerClient.GetAsync($"/api/v1/star-stories/{storyId}");
        Assert.Equal(95, (await DataAsync(currentResponse)).GetProperty("latestScore").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsync($"/api/v1/star-stories/{storyId}/evaluate", null)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var feature = await db.EntitlementFeatures.Include(item => item.Entitlement)
            .SingleAsync(item => item.Entitlement.UserId == owner.UserId && item.FeatureCode == FeatureValues.StarBuilder);
        Assert.Equal(1, feature.Consumed);
        Assert.Equal(0, feature.Reserved);
        Assert.Equal(1, await db.FeatureUsageEvents.CountAsync(item => item.UserId == owner.UserId && item.Action == FeatureValues.Void));
    }

    private static async Task<Guid> SaveAsync(HttpClient client, Guid attemptId, string title, IReadOnlyCollection<string> tags, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/star-stories")
        {
            Content = JsonContent.Create(new { sourceAttemptId = attemptId, title, tags })
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> SeedCompletedAttemptAsync(NexoraApiFactory factory, Guid userId, string suffix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var answer = "Situation: payment outage happened. Task: restore checkout. Action: checked logs and added a retry. Result: checkout recovered in ten minutes.";
        var attempt = new StarAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Question = $"Tell me about {suffix}.",
            Answer = answer,
            Status = PracticeFeatureValues.Completed,
            EvaluationJson = JsonSerializer.Serialize(InitialEvaluation(), JsonOptions),
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        db.StarAttempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt.Id;
    }

    private static StarEvaluation InitialEvaluation() => new(
        true,
        84,
        new StarComponentEvaluation(80, true, "payment outage happened", "Situation is clear."),
        new StarComponentEvaluation(80, true, "restore checkout", "Task is clear."),
        new StarComponentEvaluation(90, true, "checked logs and added a retry", "Action is specific."),
        new StarComponentEvaluation(85, true, "checkout recovered in ten minutes", "Result is measurable."),
        [], ["Clear ownership"], ["Add more context"], AiOperations.ScoreScale);

    private static StarEvaluation ValidEvaluation(int score) => new(
        true,
        null,
        new StarComponentEvaluation(score, true, "edited current situation", "Situation is clear."),
        new StarComponentEvaluation(score, true, "edited current task", "Task is clear."),
        new StarComponentEvaluation(score, true, "edited current action", "Action is specific."),
        new StarComponentEvaluation(score, true, "edited current result", "Result is measurable."),
        [], ["Clear ownership"], ["Add more context"], AiOperations.ScoreScale);

    private static async Task SeedFeatureEntitlementAsync(NexoraApiFactory factory, Guid userId, string featureCode, int limit)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"star-story-{Guid.NewGuid():N}", Name = "STAR Story test", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 1, Currency = "VND", DurationDays = 30, InterviewQuota = 1, IsActive = true, CreatedAt = now };
        var subscription = new Subscription { Id = Guid.NewGuid(), UserId = userId, Status = BillingValues.Active, StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(), UserId = userId, SubscriptionId = subscription.Id, PlanCodeSnapshot = plan.Code,
            Status = BillingValues.Active, InterviewLimit = 1, StartsAt = subscription.StartsAt, EndsAt = subscription.EndsAt,
            CreatedAt = now, UpdatedAt = now, ConcurrencyToken = Guid.NewGuid()
        };
        var definition = await db.FeatureDefinitions.SingleAsync(item => item.Code == featureCode);
        db.AddRange(plan, price, subscription, entitlement, new EntitlementFeature
        {
            Id = Guid.NewGuid(), EntitlementId = entitlement.Id, FeatureDefinitionId = definition.Id, FeatureCode = featureCode,
            IsEnabled = true, Limit = limit, CreatedAt = now, UpdatedAt = now, ConcurrencyToken = Guid.NewGuid()
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"star-story-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123", displayName = "STAR Story candidate" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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
}
