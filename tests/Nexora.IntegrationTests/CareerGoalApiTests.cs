using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Data.Career;
using Nexora.Data.Learning;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class CareerGoalApiTests
{
    private static readonly int[] ExpectedConcurrentActivationStatusCodes = [200, 409];

    [Fact]
    public async Task CareerGoalRequiresAuthenticationAndValidatesRequiredFieldsAndSeniority()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        using var guest = await client.PostAsJsonAsync("/api/v1/career-goals", new { targetRole = "Backend Developer", seniority = "junior" });
        Assert.Equal(HttpStatusCode.Unauthorized, guest.StatusCode);

        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var missing = await client.PostAsJsonAsync("/api/v1/career-goals", new { targetRole = "", seniority = "junior" });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ErrorCodeAsync(missing));

        using var invalidSeniority = await client.PostAsJsonAsync("/api/v1/career-goals", new { targetRole = "Backend Developer", seniority = "wizard" });
        Assert.Equal(HttpStatusCode.BadRequest, invalidSeniority.StatusCode);
        Assert.Equal("INVALID_SENIORITY", await ErrorCodeAsync(invalidSeniority));

        using var malformedDate = await client.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = "Backend Developer",
            seniority = "junior",
            targetDate = "not-a-date"
        });
        Assert.Equal(HttpStatusCode.BadRequest, malformedDate.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ErrorCodeAsync(malformedDate));
    }

    [Fact]
    public async Task CareerGoalsAreOwnerScopedAndNullableFieldsCanBeCleared()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);

        var ownerJdId = await CreateJobDescriptionAsync(ownerClient, "Owner JD");
        var otherJdId = await CreateJobDescriptionAsync(otherClient, "Other JD");
        using var create = await ownerClient.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = " Backend Developer ",
            seniority = "Senior",
            industry = "Fintech",
            targetCompany = "Example Bank",
            targetJobDescriptionId = ownerJdId,
            targetDate = "2027-06-30"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = await DataAsync(create);
        var goalId = created.GetProperty("id").GetGuid();
        Assert.Equal("Backend Developer", created.GetProperty("targetRole").GetString());
        Assert.Equal("senior", created.GetProperty("seniority").GetString());
        Assert.True(created.GetProperty("active").GetBoolean());
        Assert.Equal("2027-06-30", created.GetProperty("targetDate").GetString());

        using var ownerGet = await ownerClient.GetAsync($"/api/v1/career-goals/{goalId}");
        Assert.Equal(HttpStatusCode.OK, ownerGet.StatusCode);
        using var otherGet = await otherClient.GetAsync($"/api/v1/career-goals/{goalId}");
        Assert.Equal(HttpStatusCode.NotFound, otherGet.StatusCode);
        using var otherPatch = await PatchAsync(otherClient, $"/api/v1/career-goals/{goalId}", new { targetRole = "Should not change" });
        Assert.Equal(HttpStatusCode.NotFound, otherPatch.StatusCode);

        using var foreignJd = await ownerClient.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = "Data Engineer",
            seniority = "mid",
            targetJobDescriptionId = otherJdId
        });
        Assert.Equal(HttpStatusCode.NotFound, foreignJd.StatusCode);
        Assert.Equal("JOB_DESCRIPTION_NOT_FOUND", await ErrorCodeAsync(foreignJd));

        using var clear = await PatchAsync(ownerClient, $"/api/v1/career-goals/{goalId}", new
        {
            industry = (string?)null,
            targetCompany = (string?)null,
            targetJobDescriptionId = (Guid?)null,
            targetDate = (DateOnly?)null
        });
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
        var cleared = await DataAsync(clear);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("industry").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("targetCompany").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("targetJobDescriptionId").ValueKind);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("targetDate").ValueKind);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persisted = await db.CareerGoals.AsNoTracking().SingleAsync(item => item.Id == goalId);
        Assert.Null(persisted.Industry);
        Assert.Null(persisted.TargetCompany);
        Assert.Null(persisted.TargetJobDescriptionId);
        Assert.Null(persisted.TargetDate);

        using var list = await ownerClient.GetAsync("/api/v1/career-goals");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var goals = await DataAsync(list);
        Assert.Single(goals.EnumerateArray());
        Assert.Equal(goalId, goals[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task CreatingAndActivatingGoalsLeavesOnlyOneActiveAndSurvivesFreshRequest()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var firstId = await CreateCareerGoalAsync(client, "Backend Developer", "junior");
        var secondId = await CreateCareerGoalAsync(client, "Platform Engineer", "senior");
        Assert.NotEqual(firstId, secondId);
        await AssertExactlyOneActiveAsync(factory, account.UserId, secondId);

        using var activate = await PatchAsync(client, $"/api/v1/career-goals/{firstId}", new { active = true });
        Assert.True(activate.StatusCode == HttpStatusCode.OK, await activate.Content.ReadAsStringAsync());
        await AssertExactlyOneActiveAsync(factory, account.UserId, firstId);

        using var repeatActivate = await PatchAsync(client, $"/api/v1/career-goals/{firstId}", new { active = true });
        Assert.True(repeatActivate.StatusCode == HttpStatusCode.OK, await repeatActivate.Content.ReadAsStringAsync());
        await AssertExactlyOneActiveAsync(factory, account.UserId, firstId);

        using var freshClient = factory.CreateHttpsClient();
        freshClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var freshRead = await freshClient.GetAsync("/api/v1/career-goals");
        Assert.Equal(HttpStatusCode.OK, freshRead.StatusCode);
        var goals = await DataAsync(freshRead);
        Assert.Equal(2, goals.GetArrayLength());
        Assert.Equal(firstId, goals[0].GetProperty("id").GetGuid());
        Assert.True(goals[0].GetProperty("active").GetBoolean());
        Assert.False(goals[1].GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task ConcurrentActivationsCannotPersistMultipleActiveGoals()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var setupClient = factory.CreateHttpsClient();
        var account = await RegisterAsync(setupClient);
        setupClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var firstId = await CreateCareerGoalAsync(setupClient, "Backend Developer", "junior");
        var secondId = await CreateCareerGoalAsync(setupClient, "Platform Engineer", "senior");

        using var clientA = factory.CreateHttpsClient();
        using var clientB = factory.CreateHttpsClient();
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        clientB.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var results = await Task.WhenAll(
            PatchAsync(clientA, $"/api/v1/career-goals/{firstId}", new { active = true }),
            PatchAsync(clientB, $"/api/v1/career-goals/{secondId}", new { active = true }));
        Assert.All(results, response => Assert.Contains((int)response.StatusCode, ExpectedConcurrentActivationStatusCodes));
        foreach (var response in results) response.Dispose();

        await AssertExactlyOneActiveAsync(factory, account.UserId, null);
    }

    [Fact]
    public async Task DeletingCareerGoalIsOwnerScopedIdempotentAndPreservesLearningPathHistory()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);

        var goalId = await CreateCareerGoalAsync(ownerClient, "Backend Developer", "junior");
        var pathId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.LearningPaths.Add(new LearningPath
            {
                Id = pathId,
                UserId = owner.UserId,
                CareerGoalId = goalId,
                Status = "active",
                CreatedAt = now,
                UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using var crossUserDelete = await DeleteAsync(otherClient, $"/api/v1/career-goals/{goalId}", "other-user-delete");
        Assert.Equal(HttpStatusCode.NotFound, crossUserDelete.StatusCode);
        using var stillVisible = await ownerClient.GetAsync($"/api/v1/career-goals/{goalId}");
        Assert.Equal(HttpStatusCode.OK, stillVisible.StatusCode);

        using var missingKey = await DeleteAsync(ownerClient, $"/api/v1/career-goals/{goalId}", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingKey.StatusCode);
        Assert.Equal("IDEMPOTENCY_KEY_REQUIRED", await ErrorCodeAsync(missingKey));

        const string deleteKey = "owner-career-goal-delete";
        using var deleted = await DeleteAsync(ownerClient, $"/api/v1/career-goals/{goalId}", deleteKey);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var persisted = await db.CareerGoals.AsNoTracking().SingleAsync(item => item.Id == goalId);
            Assert.False(persisted.Active);
            Assert.NotNull(persisted.DeletedAt);
            Assert.True(await db.LearningPaths.AsNoTracking().AnyAsync(item => item.Id == pathId && item.CareerGoalId == goalId));
        }

        using var list = await ownerClient.GetAsync("/api/v1/career-goals");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Empty((await DataAsync(list)).EnumerateArray());

        using var hiddenGet = await ownerClient.GetAsync($"/api/v1/career-goals/{goalId}");
        Assert.Equal(HttpStatusCode.NotFound, hiddenGet.StatusCode);
        using var hiddenPatch = await PatchAsync(ownerClient, $"/api/v1/career-goals/{goalId}", new { active = true });
        Assert.Equal(HttpStatusCode.NotFound, hiddenPatch.StatusCode);

        using var repeatDelete = await DeleteAsync(ownerClient, $"/api/v1/career-goals/{goalId}", deleteKey);
        Assert.Equal(HttpStatusCode.NoContent, repeatDelete.StatusCode);
        using var newKeyDelete = await DeleteAsync(ownerClient, $"/api/v1/career-goals/{goalId}", "owner-career-goal-delete-again");
        Assert.Equal(HttpStatusCode.NotFound, newKeyDelete.StatusCode);

        using var learningPathRead = await ownerClient.GetAsync("/api/v1/learning-path");
        Assert.Equal(HttpStatusCode.BadRequest, learningPathRead.StatusCode);
        Assert.Equal("ACTIVE_CAREER_GOAL_REQUIRED", await ErrorCodeAsync(learningPathRead));

        using var export = await ownerClient.GetAsync("/api/v1/me/export");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var exportedGoals = (await DataAsync(export)).GetProperty("careerGoals").EnumerateArray().ToArray();
        Assert.Contains(exportedGoals, item => item.GetProperty("id").GetGuid() == goalId);

        var newGoalId = await CreateCareerGoalAsync(ownerClient, "Platform Engineer", "senior");
        await AssertExactlyOneActiveAsync(factory, owner.UserId, newGoalId);
        using var reusedKey = await DeleteAsync(ownerClient, $"/api/v1/career-goals/{newGoalId}", deleteKey);
        Assert.Equal(HttpStatusCode.Conflict, reusedKey.StatusCode);
        Assert.Equal("IDEMPOTENCY_CONFLICT", await ErrorCodeAsync(reusedKey));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(1, await db.IdempotencyRecords.CountAsync(item =>
                item.ActorId == owner.UserId && item.Operation == "career-goal.delete" && item.Key == deleteKey));
        }
    }

    private static async Task AssertExactlyOneActiveAsync(NexoraApiFactory factory, Guid userId, Guid? expectedId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var active = await db.CareerGoals.AsNoTracking().Where(item => item.UserId == userId && item.Active).ToArrayAsync();
        Assert.Single(active);
        if (expectedId.HasValue) Assert.Equal(expectedId.Value, active[0].Id);
    }

    private static async Task<Guid> CreateCareerGoalAsync(HttpClient client, string targetRole, string seniority)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/career-goals", new { targetRole, seniority });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateJobDescriptionAsync(HttpClient client, string title)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/job-descriptions", new
        {
            title,
            content = $"A private job description for {title}."
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, object body)
    {
        return await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(body)
        });
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, string path, string? idempotencyKey)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, path);
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return await client.SendAsync(request);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"career-goal-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Career goal candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
