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

public sealed class LearningPathApiTests
{
    [Fact]
    public async Task LearningPathRequiresAuthentication()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile()));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/v1/learning-path");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task LearningPathRequiresAnActiveCareerGoal()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile()));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);

        using var response = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var error = await ErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("ACTIVE_CAREER_GOAL_REQUIRED", error.GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().LearningPaths.CountAsync());
    }

    [Fact]
    public async Task GetDoesNotCreateAPathWhenTheCurrentGoalHasNoPath()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile()));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");

        using var response = await client.GetAsync("/api/v1/learning-path");
        var error = await ErrorAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LEARNING_PATH_NOT_FOUND", error.GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().LearningPaths.CountAsync());
    }

    [Fact]
    public async Task GenerateMapsValidatedSkillProfileGapsToDeterministicActivities()
    {
        var scenarioId = Guid.Parse("61000000-0000-0000-0000-000000000001");
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("scenario.problem_solving", "Problem Solving", "scenario", 40),
            Competency("behavioral.action", "Action", "behavioral", 61),
            Competency("interview.communication", "Communication", "interview", 74),
            Competency("resume.clarity", "Clarity", "resume", 75),
            new SkillProfileWeaknessSignal("cv_analysis", "SQL", DateTimeOffset.UtcNow))));
        factory.InitializeDatabase();
        await SeedScenarioAsync(factory, scenarioId, "Problem Solving");
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var careerGoalId = await CreateCareerGoalAsync(client, "Backend Developer");

        using var response = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var data = await DataAsync(response);
        var activities = Activities(data).ToArray();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(careerGoalId, data.GetProperty("careerGoalId").GetGuid());
        Assert.Equal(LearningPathValues.Active, data.GetProperty("status").GetString());
        Assert.Equal(
            [LearningPathValues.Scenario, LearningPathValues.Interview, LearningPathValues.StarDrill, LearningPathValues.ResumeImprovement],
            activities.Select(item => item.GetProperty("type").GetString()));
        Assert.Contains(activities, item => item.GetProperty("resourceId").GetGuid() == scenarioId);
        Assert.Contains(activities, item => item.GetProperty("competencyCode").GetString() == "interview.communication");
        Assert.Contains(activities, item => item.GetProperty("competencyCode").GetString() == "behavioral.action");
        Assert.Contains(activities, item => item.GetProperty("competencyCode").GetString() is null);
        Assert.DoesNotContain(activities, item => item.GetProperty("competencyCode").GetString() == "resume.clarity");
        Assert.All(activities, item => Assert.Null(item.GetProperty("externalUrl").GetString()));
        Assert.Equal(4, data.GetProperty("progress").GetProperty("totalActivityCount").GetInt32());
    }

    [Fact]
    public async Task GenerateIsIdempotentAndDataSurvivesASecondRequest()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");

        using var firstResponse = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var first = await DataAsync(firstResponse);
        using var secondResponse = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var second = await DataAsync(secondResponse);
        using var getResponse = await client.GetAsync("/api/v1/learning-path");
        var persisted = await DataAsync(getResponse);

        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        Assert.Equal(first.GetProperty("id").GetGuid(), persisted.GetProperty("id").GetGuid());
        Assert.Equal(
            Activities(first).Select(item => item.GetProperty("id").GetGuid()),
            Activities(persisted).Select(item => item.GetProperty("id").GetGuid()));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.LearningPaths.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(1, await db.LearningPathActivities.CountAsync());
    }

    [Fact]
    public async Task GenerateWithNoEvidenceReturnsAnEmptyPathWithoutInventedActivities()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile()));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");

        using var response = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var data = await DataAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(data.GetProperty("milestones").EnumerateArray());
        Assert.Equal(0, data.GetProperty("progress").GetProperty("totalActivityCount").GetInt32());
        Assert.Equal(0, data.GetProperty("progress").GetProperty("percentage").GetInt32());
    }

    [Fact]
    public async Task OwnerCanCompleteAnActivityAndRepeatedCompletionIsIdempotent()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50),
            Competency("interview.communication", "Communication", "interview", 70))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var generatedData = await DataAsync(generated);
        var activityId = Activities(generatedData).First().GetProperty("id").GetGuid();

        using var completedResponse = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{activityId}",
            new { status = "completed" });
        var completed = await DataAsync(completedResponse);
        var completedAt = Activities(completed).Single(item => item.GetProperty("id").GetGuid() == activityId)
            .GetProperty("completedAt").GetDateTimeOffset();
        using var repeatedResponse = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{activityId}",
            new { status = "completed" });
        var repeated = await DataAsync(repeatedResponse);

        Assert.Equal(HttpStatusCode.OK, completedResponse.StatusCode);
        Assert.Equal(1, completed.GetProperty("progress").GetProperty("completedActivityCount").GetInt32());
        Assert.Equal(completedAt, Activities(repeated).Single(item => item.GetProperty("id").GetGuid() == activityId)
            .GetProperty("completedAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task ActivityUpdateRejectsUnsupportedTransitionsAndMissingActivitiesSafely()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var path = await DataAsync(generated);
        var activityId = Activities(path).Single().GetProperty("id").GetGuid();

        using var invalid = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{activityId}",
            new { status = "pending" });
        var invalidError = await ErrorAsync(invalid);
        using var missing = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{Guid.NewGuid()}",
            new { status = "completed" });
        var missingError = await ErrorAsync(missing);

        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("LEARNING_PATH_ACTIVITY_STATUS_INVALID", invalidError.GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("LEARNING_PATH_ACTIVITY_NOT_FOUND", missingError.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ForeignUserCannotReadOrUpdateAnotherUsersPathActivity()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        Authorize(ownerClient, owner);
        await CreateCareerGoalAsync(ownerClient, "Backend Developer");
        using var generated = await ownerClient.PostAsJsonAsync("/api/v1/learning-path", new { });
        var path = await DataAsync(generated);
        var activityId = Activities(path).Single().GetProperty("id").GetGuid();

        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient);
        Authorize(otherClient, other);
        using var get = await otherClient.GetAsync("/api/v1/learning-path");
        using var update = await otherClient.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{activityId}",
            new { status = "completed" });
        var updateError = await ErrorAsync(update);

        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal("LEARNING_PATH_ACTIVITY_NOT_FOUND", updateError.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RefreshPreservesCompletedHistoryObsoletesResolvedPendingWorkAndAddsNewGaps()
    {
        var service = new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50),
            Competency("interview.communication", "Communication", "interview", 70)));
        using var factory = NewFactory(service);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var original = await DataAsync(generated);
        var originalActivities = Activities(original).ToArray();
        var completedId = originalActivities.Single(item => item.GetProperty("competencyCode").GetString() == "resume.clarity")
            .GetProperty("id").GetGuid();
        var resolvedPendingId = originalActivities.Single(item => item.GetProperty("competencyCode").GetString() == "interview.communication")
            .GetProperty("id").GetGuid();
        using var completed = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{completedId}",
            new { status = "completed" });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);

        service.Current = Profile(Competency("behavioral.action", "Action", "behavioral", 40));
        using var refreshedResponse = await client.PostAsJsonAsync("/api/v1/learning-path/refresh", new { });
        var refreshed = await DataAsync(refreshedResponse);
        var refreshedActivities = Activities(refreshed).ToArray();

        var completedActivity = refreshedActivities.Single(item => item.GetProperty("id").GetGuid() == completedId);
        Assert.Equal("completed", completedActivity.GetProperty("status").GetString());
        Assert.NotNull(completedActivity.GetProperty("completedAt").GetString());
        Assert.Equal("obsolete", refreshedActivities.Single(item => item.GetProperty("id").GetGuid() == resolvedPendingId)
            .GetProperty("status").GetString());
        Assert.Contains(refreshedActivities, item => item.GetProperty("competencyCode").GetString() == "behavioral.action");
        Assert.Equal(1, refreshed.GetProperty("progress").GetProperty("completedActivityCount").GetInt32());
        Assert.Equal(2, refreshed.GetProperty("progress").GetProperty("totalActivityCount").GetInt32());
    }

    [Fact]
    public async Task RefreshWithUnchangedInputsKeepsIdsAndTimestampsStable()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var original = await DataAsync(generated);
        using var beforeScope = factory.Services.CreateScope();
        var beforeDb = beforeScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var beforeActivityUpdates = await beforeDb.LearningPathActivities.AsNoTracking()
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.UpdatedAt }).ToArrayAsync();
        using var refreshedResponse = await client.PostAsJsonAsync("/api/v1/learning-path/refresh", new { });
        var refreshed = await DataAsync(refreshedResponse);
        using var afterScope = factory.Services.CreateScope();
        var afterDb = afterScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var afterActivityUpdates = await afterDb.LearningPathActivities.AsNoTracking()
            .OrderBy(item => item.Id).Select(item => new { item.Id, item.UpdatedAt }).ToArrayAsync();

        Assert.Equal(original.GetProperty("id").GetGuid(), refreshed.GetProperty("id").GetGuid());
        Assert.Equal(original.GetProperty("updatedAt").GetDateTimeOffset(), refreshed.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal(
            Activities(original).Select(item => item.GetProperty("id").GetGuid()),
            Activities(refreshed).Select(item => item.GetProperty("id").GetGuid()));
        Assert.Equal(beforeActivityUpdates, afterActivityUpdates);
    }

    [Fact]
    public async Task RefreshCreatesTheCurrentPathWhenInitialGenerationWasSkipped()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");

        using var response = await client.PostAsJsonAsync("/api/v1/learning-path/refresh", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().LearningPaths.CountAsync());
    }

    [Fact]
    public async Task SwitchingCareerGoalCreatesASeparatePathAndPreservesThePreviousPath()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var firstGoalId = await CreateCareerGoalAsync(client, "Backend Developer");
        using var firstResponse = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var firstPath = await DataAsync(firstResponse);
        var secondGoalId = await CreateCareerGoalAsync(client, "Platform Engineer");
        using var secondResponse = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var secondPath = await DataAsync(secondResponse);

        Assert.Equal(firstGoalId, firstPath.GetProperty("careerGoalId").GetGuid());
        Assert.Equal(secondGoalId, secondPath.GetProperty("careerGoalId").GetGuid());
        Assert.NotEqual(firstPath.GetProperty("id").GetGuid(), secondPath.GetProperty("id").GetGuid());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(2, await db.LearningPaths.CountAsync(item => item.UserId == account.UserId));
        Assert.False(await db.CareerGoals.Where(item => item.Id == firstGoalId).Select(item => item.Active).SingleAsync());
        Assert.True(await db.CareerGoals.Where(item => item.Id == secondGoalId).Select(item => item.Active).SingleAsync());
    }

    [Fact]
    public async Task PrivacyExportIncludesLearningPathAndDeletionRemovesItsRows()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var path = await DataAsync(generated);

        using var export = await client.GetAsync("/api/v1/me/export");
        var exported = await DataAsync(export);
        using var deletion = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/deletion-requests");
        deletion.Headers.Add("Idempotency-Key", $"learning-path-delete-{Guid.NewGuid():N}");
        using var deletionResponse = await client.SendAsync(deletion);
        using var scope = factory.Services.CreateScope();
        var processed = await scope.ServiceProvider.GetRequiredService<Nexora.Business.Privacy.IPrivacyJobProcessor>()
            .ProcessPendingAsync(CancellationToken.None);
        using var finalScope = factory.Services.CreateScope();
        var db = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(path.GetProperty("id").GetGuid(), exported.GetProperty("learningPaths")[0].GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.Accepted, deletionResponse.StatusCode);
        Assert.Equal(1, processed);
        Assert.Equal(0, await db.LearningPathActivities.CountAsync(item => item.LearningPathId == path.GetProperty("id").GetGuid()));
        Assert.Equal(0, await db.LearningPathMilestones.CountAsync(item => item.LearningPathId == path.GetProperty("id").GetGuid()));
        Assert.Equal(0, await db.LearningPaths.CountAsync(item => item.UserId == account.UserId));
    }

    [Fact]
    public async Task ConcurrentGenerationLeavesOnePathForTheCurrentGoal()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var setupClient = factory.CreateHttpsClient();
        var account = await RegisterAsync(setupClient);
        Authorize(setupClient, account);
        await CreateCareerGoalAsync(setupClient, "Backend Developer");
        using var firstClient = factory.CreateHttpsClient();
        using var secondClient = factory.CreateHttpsClient();
        Authorize(firstClient, account);
        Authorize(secondClient, account);

        var responses = await Task.WhenAll(
            firstClient.PostAsJsonAsync("/api/v1/learning-path", new { }),
            secondClient.PostAsJsonAsync("/api/v1/learning-path", new { }));

        Assert.All(responses, response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().LearningPaths.CountAsync(item => item.UserId == account.UserId));
    }

    [Fact]
    public async Task ActivityPatchRequiresTheSupportedStatusField()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var path = await DataAsync(generated);
        var activityId = Activities(path).Single().GetProperty("id").GetGuid();

        using var response = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{activityId}",
            new { });
        var error = await ErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CompletingTheOnlyActivityCompletesItsMilestoneAndReachesFullProgress()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var path = await DataAsync(generated);
        var activityId = Activities(path).Single().GetProperty("id").GetGuid();

        using var response = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{activityId}",
            new { status = "completed" });
        var completed = await DataAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("completed", completed.GetProperty("milestones")[0].GetProperty("status").GetString());
        Assert.Equal(100, completed.GetProperty("progress").GetProperty("percentage").GetInt32());
    }

    [Fact]
    public async Task ObsoleteActivityCannotBeCompleted()
    {
        var service = new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50)));
        using var factory = NewFactory(service);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var generated = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var path = await DataAsync(generated);
        var activityId = Activities(path).Single().GetProperty("id").GetGuid();
        service.Current = Profile();
        using var refresh = await client.PostAsJsonAsync("/api/v1/learning-path/refresh", new { });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);

        using var response = await client.PatchAsJsonAsync(
            $"/api/v1/learning-path/activities/{activityId}",
            new { status = "completed" });
        var error = await ErrorAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("LEARNING_PATH_ACTIVITY_OBSOLETE", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task RefreshWithTheSameInputsDoesNotDuplicateActivities()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50),
            Competency("interview.communication", "Communication", "interview", 70))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var first = await client.PostAsJsonAsync("/api/v1/learning-path/refresh", new { });
        var firstPath = await DataAsync(first);
        using var second = await client.PostAsJsonAsync("/api/v1/learning-path/refresh", new { });
        var secondPath = await DataAsync(second);

        Assert.Equal(firstPath.GetProperty("id").GetGuid(), secondPath.GetProperty("id").GetGuid());
        Assert.Equal(2, Activities(secondPath).Count());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.LearningPaths.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(2, await db.LearningPathActivities.CountAsync());
        Assert.Equal(2, await db.LearningPathMilestones.CountAsync());
    }

    [Fact]
    public async Task ScenarioGapWithoutPublishedResourceIsSkippedSafely()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("scenario.problem_solving", "Problem Solving", "scenario", 40))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");

        using var response = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var data = await DataAsync(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(Activities(data));
    }

    [Fact]
    public async Task QualitativeCvSignalCreatesOnlyAResumeImprovementActivity()
    {
        using var factory = NewFactory(new MutableSkillProfileService(new SkillProfileView(
            [],
            [new SkillProfileWeaknessSignal("cv_analysis", "Missing SQL evidence", DateTimeOffset.UtcNow)])));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");

        using var response = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        var data = await DataAsync(response);
        var activity = Assert.Single(Activities(data));

        Assert.Equal("resume_improvement", activity.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, activity.GetProperty("competencyCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, activity.GetProperty("resourceId").ValueKind);
        Assert.Equal(3, activity.GetProperty("priority").GetInt32());
    }

    [Fact]
    public async Task SeparateOwnersReceiveSeparatePathsWithNoCrossOwnerRows()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        Authorize(ownerClient, owner);
        await CreateCareerGoalAsync(ownerClient, "Backend Developer");
        using var ownerResponse = await ownerClient.PostAsJsonAsync("/api/v1/learning-path", new { });
        var ownerPath = await DataAsync(ownerResponse);

        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient);
        Authorize(otherClient, other);
        await CreateCareerGoalAsync(otherClient, "Backend Developer");
        using var otherResponse = await otherClient.PostAsJsonAsync("/api/v1/learning-path", new { });
        var otherPath = await DataAsync(otherResponse);

        Assert.NotEqual(ownerPath.GetProperty("id").GetGuid(), otherPath.GetProperty("id").GetGuid());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.LearningPaths.CountAsync(item => item.UserId == owner.UserId));
        Assert.Equal(1, await db.LearningPaths.CountAsync(item => item.UserId == other.UserId));
        Assert.Equal(1, await db.LearningPathActivities.CountAsync(item =>
            item.LearningPath.UserId == owner.UserId));
        Assert.Equal(1, await db.LearningPathActivities.CountAsync(item =>
            item.LearningPath.UserId == other.UserId));
    }

    [Fact]
    public async Task SwitchingGoalsDoesNotMakeGetReturnThePreviousGoalsPath()
    {
        using var factory = NewFactory(new MutableSkillProfileService(Profile(
            Competency("resume.clarity", "Clarity", "resume", 50))));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client, "Backend Developer");
        using var first = await client.PostAsJsonAsync("/api/v1/learning-path", new { });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await CreateCareerGoalAsync(client, "Platform Engineer");

        using var response = await client.GetAsync("/api/v1/learning-path");
        var error = await ErrorAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LEARNING_PATH_NOT_FOUND", error.GetProperty("code").GetString());
        using var scope = factory.Services.CreateScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<NexoraDbContext>()
            .LearningPaths.CountAsync(item => item.UserId == account.UserId));
    }

    private static NexoraApiFactory NewFactory(MutableSkillProfileService service) =>
        new(new Dictionary<string, string?>(), services =>
        {
            services.RemoveAll<ISkillProfileService>();
            services.AddSingleton<ISkillProfileService>(service);
        });

    private static SkillProfileView Profile(params SkillProfileCompetency[] competencies) =>
        new(competencies, []);

    private static SkillProfileView Profile(
        SkillProfileCompetency first,
        SkillProfileWeaknessSignal weakness) =>
        new([first], [weakness]);

    private static SkillProfileView Profile(
        SkillProfileCompetency first,
        SkillProfileCompetency second,
        SkillProfileCompetency third,
        SkillProfileCompetency fourth,
        SkillProfileWeaknessSignal weakness) =>
        new([first, second, third, fourth], [weakness]);

    private static SkillProfileCompetency Competency(string code, string name, string category, int score) =>
        new(code, name, category, score, 1, DateTimeOffset.UtcNow, []);

    private static void Authorize(HttpClient client, Account account) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

    private static async Task<Guid> CreateCareerGoalAsync(HttpClient client, string role)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = role,
            seniority = "senior"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await DataAsync(response);
        return data.GetProperty("id").GetGuid();
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
            Slug = $"learning-path-{scenarioId:N}",
            Title = "Learning path scenario",
            Summary = "A deterministic published scenario.",
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

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"learning-path-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Learning path candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
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
}
