using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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
            Id = Guid.NewGuid(), UserId = account.UserId, Title = "Secret JD", Content = "Secret JD body", Version = 1,
            CreatedAt = now, UpdatedAt = now
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
            Id = Guid.NewGuid(), Slug = $"secret-{Guid.NewGuid():N}", Title = "Secret scenario", Summary = "Not published",
            CategoryId = category.Id, Difficulty = "medium", Competency = "problem", EstimatedMinutes = 10,
            Content = "Secret body", SortOrder = 99, Status = "draft", CreatedAt = now, UpdatedAt = now
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
        var ef = await db.EntitlementFeatures.Include(item => item.Entitlement).SingleAsync(item => item.Entitlement.UserId == account.UserId && item.FeatureCode == FeatureValues.CvAnalysis);
        Assert.Equal(0, ef.Adjustment);
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

    private static async Task<Nexora.Data.Practice.Scenario> SeedPublishedScenarioAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var category = await db.ScenarioCategories.FirstAsync();
        var scenario = new Nexora.Data.Practice.Scenario
        {
            Id = Guid.NewGuid(), Slug = $"scenario-{Guid.NewGuid():N}", Title = "Scenario test", Summary = "Published scenario for tests",
            CategoryId = category.Id, Difficulty = "medium", Competency = "problem_analysis", EstimatedMinutes = 15,
            Content = "A rich scenario body describing a business problem to solve.", SortOrder = 1, Status = "published",
            CreatedAt = now, UpdatedAt = now, PublishedAt = now
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
            Id = Guid.NewGuid(), UserId = userId, SubscriptionId = subscription.Id, PlanCodeSnapshot = plan.Code,
            Status = BillingValues.Active, InterviewLimit = 1, StartsAt = subscription.StartsAt, EndsAt = subscription.EndsAt,
            CreatedAt = now, UpdatedAt = now, ConcurrencyToken = Guid.NewGuid()
        };
        var featureDef = await db.FeatureDefinitions.SingleAsync(item => item.Code == featureCode);
        var ef = new EntitlementFeature
        {
            Id = Guid.NewGuid(), EntitlementId = entitlement.Id, FeatureDefinitionId = featureDef.Id, FeatureCode = featureCode,
            IsEnabled = true, Limit = limit, CreatedAt = now, UpdatedAt = now, ConcurrencyToken = Guid.NewGuid()
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
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"platform-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "Platform candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private sealed record Account(Guid UserId, string AccessToken);
    private sealed record AdminAccount(string AccessToken);
    private sealed record Grant(Guid EntitlementId);

    private sealed class FailingAiProvider : Nexora.Business.Ai.IAiProvider
    {
        public string ModelVersion => "test-gemini-model";
        public Task<T> GenerateStructuredAsync<T>(Nexora.Business.Ai.AiRequest request, CancellationToken cancellationToken) =>
            Task.FromException<T>(new TimeoutException("Deterministic failure"));
    }
}
