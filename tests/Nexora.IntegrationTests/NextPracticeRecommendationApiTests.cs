using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Skills;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class NextPracticeRecommendationApiTests
{
    private static readonly DateTimeOffset OldEvidence = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NewEvidence = new(2026, 9, 10, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task NextRecommendationRequiresAuthentication()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile()));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/v1/recommendations/next");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MissingLearningPathPreservesB11NotFoundBehavior()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile()));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client);

        using var response = await client.GetAsync("/api/v1/recommendations/next");
        var error = await ErrorAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LEARNING_PATH_NOT_FOUND", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task NoPendingActivitiesReturnsNullableEmptyDataWithoutCreatingRows()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50, 1, OldEvidence))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client);
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var path = await DataAsync(generated);
        var activityId = Activities(path).Single().GetProperty("id").GetGuid();
        using var complete = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{activityId}", new { status = LearningPathValues.Completed });
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);

        var beforeCount = await CountActivitiesAsync(factory, account.UserId);
        using var response = await client.GetAsync("/api/v1/recommendations/next");
        var data = await DataAsync(response);
        var afterCount = await CountActivitiesAsync(factory, account.UserId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, data.ValueKind);
        Assert.Equal(beforeCount, afterCount);
    }

    [Fact]
    public async Task PriorityOneBeatsPriorityTwo()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 70, 1, OldEvidence),
            Competency("interview.structure", "Structure", "interview", 40, 1, OldEvidence))));
        factory.InitializeDatabase();
        using var client = await AuthenticatedClientAsync(factory);
        await CreateCareerGoalAsync(client);
        await GeneratePathAsync(client);

        var data = await GetRecommendationAsync(client);

        Assert.Equal(1, data.GetProperty("priority").GetInt32());
        Assert.Equal("interview", data.GetProperty("activityType").GetString());
        Assert.Contains("Structure", data.GetProperty("reason").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StrongerEvidenceWinsWithinTheSamePriority()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 40, 1, OldEvidence),
            Competency("interview.structure", "Structure", "interview", 50, 3, OldEvidence))));
        factory.InitializeDatabase();
        using var client = await AuthenticatedClientAsync(factory);
        await CreateCareerGoalAsync(client);
        await GeneratePathAsync(client);

        var data = await GetRecommendationAsync(client);

        Assert.Contains("Structure", data.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains("3 evidence items", data.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var rationale = data.GetProperty("rationale");
        Assert.Equal("interview.structure", rationale.GetProperty("competencyCode").GetString());
        Assert.Equal("Structure", rationale.GetProperty("competencyName").GetString());
        Assert.Equal(3, rationale.GetProperty("evidenceCount").GetInt32());
        Assert.False(rationale.GetProperty("hasMoreRecentlyPracticedPeer").GetBoolean());
    }

    [Fact]
    public async Task LessRecentlyPracticedCompetencyWinsWhenEvidenceTies()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 40, 2, NewEvidence),
            Competency("interview.structure", "Structure", "interview", 40, 2, OldEvidence))));
        factory.InitializeDatabase();
        using var client = await AuthenticatedClientAsync(factory);
        await CreateCareerGoalAsync(client);
        await GeneratePathAsync(client);

        var data = await GetRecommendationAsync(client);

        Assert.Contains("Structure", data.GetProperty("reason").GetString(), StringComparison.Ordinal);
        var rationale = data.GetProperty("rationale");
        Assert.Equal("interview.structure", rationale.GetProperty("competencyCode").GetString());
        Assert.True(rationale.GetProperty("hasMoreRecentlyPracticedPeer").GetBoolean());
    }

    [Fact]
    public async Task CompletedAndObsoleteActivitiesAreNeverRecommended()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 40, 1, OldEvidence),
            Competency("interview.structure", "Structure", "interview", 70, 1, OldEvidence))));
        factory.InitializeDatabase();
        using var client = await AuthenticatedClientAsync(factory);
        await CreateCareerGoalAsync(client);
        var path = await GeneratePathAsync(client);
        var activities = Activities(path).ToArray();
        var clarityId = activities.Single(item => item.GetProperty("competencyCode").GetString() == "resume.clarity").GetProperty("id").GetGuid();
        var structureId = activities.Single(item => item.GetProperty("competencyCode").GetString() == "interview.structure").GetProperty("id").GetGuid();
        await SetActivityStatusAsync(factory, clarityId, LearningPathValues.Completed);
        await SetActivityStatusAsync(factory, structureId, LearningPathValues.Obsolete);

        var data = await GetRecommendationAsync(client);

        Assert.Equal(JsonValueKind.Null, data.ValueKind);
    }

    [Fact]
    public async Task StalePendingCompetencyIsExcludedWithoutRefreshingLearningPath()
    {
        var service = new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50, 1, OldEvidence)));
        using var factory = NewFactory(service);
        factory.InitializeDatabase();
        using var client = await AuthenticatedClientAsync(factory);
        await CreateCareerGoalAsync(client);
        await GeneratePathAsync(client);
        var beforeCount = await CountActivitiesAsync(factory, (await CurrentAccountAsync(client)).UserId);
        service.Current = Profile(Competency("resume.clarity", "Clarity", "resume", 80, 1, NewEvidence));

        var data = await GetRecommendationAsync(client);
        var afterCount = await CountActivitiesAsync(factory, (await CurrentAccountAsync(client)).UserId);

        Assert.Equal(JsonValueKind.Null, data.ValueKind);
        Assert.Equal(beforeCount, afterCount);
    }

    [Fact]
    public async Task ScenarioRecommendationReturnsTheRealResourceId()
    {
        var scenarioId = Guid.Parse("71000000-0000-0000-0000-000000000001");
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("scenario.problem_solving", "Problem Solving", "scenario", 40, 1, OldEvidence))));
        factory.InitializeDatabase();
        await SeedScenarioAsync(factory, scenarioId, "Problem Solving");
        using var client = await AuthenticatedClientAsync(factory);
        await CreateCareerGoalAsync(client);
        await GeneratePathAsync(client);

        var data = await GetRecommendationAsync(client);

        Assert.Equal(LearningPathValues.Scenario, data.GetProperty("activityType").GetString());
        Assert.Equal(scenarioId, data.GetProperty("resourceId").GetGuid());
    }

    [Fact]
    public async Task ScenarioWithoutResourceUsesExternalLearningAndNullResourceId()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("scenario.problem_solving", "Problem Solving", "scenario", 40, 1, OldEvidence))));
        factory.InitializeDatabase();
        using var client = await AuthenticatedClientAsync(factory);
        await CreateCareerGoalAsync(client);
        await GeneratePathAsync(client);

        var data = await GetRecommendationAsync(client);

        Assert.Equal(LearningPathValues.ExternalLearning, data.GetProperty("activityType").GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("resourceId").ValueKind);
        Assert.Equal(20, data.GetProperty("estimatedMinutes").GetInt32());
    }

    [Theory]
    [InlineData("behavioral.action", "Action", "star_drill", 15)]
    [InlineData("interview.structure", "Structure", "interview", 20)]
    [InlineData("resume.clarity", "Clarity", "resume_improvement", 15)]
    public async Task RecommendationUsesDurationForEachB11ActivityType(
        string code,
        string name,
        string expectedType,
        int expectedMinutes)
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency(code, name, code[..code.IndexOf('.')], 40, 1, OldEvidence))));
        factory.InitializeDatabase();
        using var client = await AuthenticatedClientAsync(factory);
        await CreateCareerGoalAsync(client);
        await GeneratePathAsync(client);

        var data = await GetRecommendationAsync(client);

        Assert.Equal(expectedType, data.GetProperty("activityType").GetString());
        Assert.Equal(expectedMinutes, data.GetProperty("estimatedMinutes").GetInt32());
    }

    [Fact]
    public async Task OtherUsersDataCannotInfluenceSelection()
    {
        var profiles = new PerUserSkillProfileService();
        using var factory = NewFactory(profiles);
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        Authorize(ownerClient, owner);
        profiles.Set(owner.UserId, Profile(Competency("resume.clarity", "Clarity", "resume", 70, 1, OldEvidence)));
        await CreateCareerGoalAsync(ownerClient);
        await GeneratePathAsync(ownerClient);

        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient);
        Authorize(otherClient, other);
        profiles.Set(other.UserId, Profile(Competency("interview.structure", "Structure", "interview", 40, 1, OldEvidence)));
        await CreateCareerGoalAsync(otherClient);
        await GeneratePathAsync(otherClient);

        var ownerRecommendation = await GetRecommendationAsync(ownerClient);
        var otherRecommendation = await GetRecommendationAsync(otherClient);

        Assert.Equal(2, ownerRecommendation.GetProperty("priority").GetInt32());
        Assert.Equal("resume_improvement", ownerRecommendation.GetProperty("activityType").GetString());
        Assert.Equal(1, otherRecommendation.GetProperty("priority").GetInt32());
        Assert.Equal("interview", otherRecommendation.GetProperty("activityType").GetString());
    }

    [Fact]
    public async Task RepeatedGetIsDeterministicAndDoesNotMutateLearningPathRows()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50, 1, OldEvidence))));
        factory.InitializeDatabase();
        using var client = await AuthenticatedClientAsync(factory);
        var account = await CurrentAccountAsync(client);
        await CreateCareerGoalAsync(client);
        await GeneratePathAsync(client);
        var beforeUpdatedAt = await PathUpdatedAtAsync(factory, account.UserId);
        var beforeCount = await CountActivitiesAsync(factory, account.UserId);

        using var first = await client.GetAsync("/api/v1/recommendations/next");
        var firstBody = await first.Content.ReadAsStringAsync();
        using var second = await client.GetAsync("/api/v1/recommendations/next");
        var secondBody = await second.Content.ReadAsStringAsync();
        var afterUpdatedAt = await PathUpdatedAtAsync(factory, account.UserId);
        var afterCount = await CountActivitiesAsync(factory, account.UserId);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(firstBody, secondBody);
        Assert.Equal(beforeUpdatedAt, afterUpdatedAt);
        Assert.Equal(beforeCount, afterCount);
    }

    private static NexoraApiFactory NewFactory(ISkillProfileService service) =>
        new(new Dictionary<string, string?>(), services =>
        {
            services.RemoveAll<ISkillProfileService>();
            services.AddSingleton<ISkillProfileService>(service);
        });

    private static SkillProfileView Profile(params SkillProfileCompetency[] competencies) =>
        new(competencies, []);

    private static SkillProfileCompetency Competency(
        string code,
        string name,
        string category,
        int score,
        int evidenceCount,
        DateTimeOffset latestEvidenceAt) =>
        new(code, name, category, score, evidenceCount, latestEvidenceAt, []);

    private static void Authorize(HttpClient client, Account account) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

    private static async Task<HttpClient> AuthenticatedClientAsync(NexoraApiFactory factory)
    {
        var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        client.DefaultRequestHeaders.Add("X-Test-User-Id", account.UserId.ToString());
        return client;
    }

    private static async Task<Account> CurrentAccountAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/me");
        var data = await DataAsync(response);
        return new Account(data.GetProperty("id").GetGuid(), client.DefaultRequestHeaders.Authorization!.Parameter!);
    }

    private static async Task<Guid> CreateCareerGoalAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = "Backend Developer",
            seniority = "senior"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GeneratePathAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK });
        return await DataAsync(response);
    }

    private static async Task<JsonElement> GetRecommendationAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/recommendations/next");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await DataAsync(response);
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"recommendation-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Recommendation candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task SeedScenarioAsync(NexoraApiFactory factory, Guid scenarioId, string competency)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var category = await db.ScenarioCategories.SingleAsync(item => item.Slug == "banking");
        var now = DateTimeOffset.UtcNow;
        db.Scenarios.Add(new Scenario
        {
            Id = scenarioId,
            Slug = $"recommendation-{scenarioId:N}",
            Title = "Recommendation scenario",
            Summary = "A deterministic recommendation scenario.",
            CategoryId = category.Id,
            Difficulty = "medium",
            Competency = competency,
            EstimatedMinutes = 15,
            Content = "Scenario content.",
            SortOrder = 1,
            Status = PracticeFeatureValues.Published,
            CreatedAt = now,
            UpdatedAt = now,
            PublishedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static async Task SetActivityStatusAsync(NexoraApiFactory factory, Guid activityId, string status)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var activity = await db.LearningPathActivities.SingleAsync(item => item.Id == activityId);
        activity.Status = status;
        activity.CompletedAt = status == LearningPathValues.Completed ? OldEvidence : null;
        await db.SaveChangesAsync();
    }

    private static async Task<int> CountActivitiesAsync(NexoraApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().LearningPathActivities
            .CountAsync(item => item.LearningPath.UserId == userId);
    }

    private static async Task<DateTimeOffset> PathUpdatedAtAsync(NexoraApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().LearningPaths
            .Where(item => item.UserId == userId).Select(item => item.UpdatedAt).SingleAsync();
    }

    private static IEnumerable<JsonElement> Activities(JsonElement path) =>
        path.GetProperty("milestones").EnumerateArray().SelectMany(item => item.GetProperty("activities").EnumerateArray());

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);

    private sealed class MutableSkillProfileService(SkillProfileView initial) : ISkillProfileService
    {
        public SkillProfileView Current { get; set; } = initial;

        public Task<SkillProfileView> GetAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(Current);
    }

    private sealed class PerUserSkillProfileService : ISkillProfileService
    {
        private readonly Dictionary<Guid, SkillProfileView> profiles = [];

        public void Set(Guid userId, SkillProfileView profile) => profiles[userId] = profile;

        public Task<SkillProfileView> GetAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult(profiles.TryGetValue(userId, out var profile) ? profile : new SkillProfileView([], []));
    }
}
