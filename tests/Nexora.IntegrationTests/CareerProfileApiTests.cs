using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Ai;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Skills;
using Nexora.Data.Career;
using Nexora.Data.Learning;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class CareerProfileApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task CareerProfileRequiresAuthentication()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/v1/me/career-profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task NewUserGetsEmptyCareerProfileWithoutCreatingPersistence()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "Empty career profile candidate");
        Authorize(client, account);

        using var response = await client.GetAsync("/api/v1/me/career-profile");
        var data = await DataAsync(response);

        Assert.Equal(account.UserId, data.GetProperty("profile").GetProperty("userId").GetGuid());
        Assert.Equal(account.Email, data.GetProperty("profile").GetProperty("email").GetString());
        Assert.Equal("Empty career profile candidate", data.GetProperty("profile").GetProperty("displayName").GetString());
        Assert.Null(data.GetProperty("profile").GetProperty("yearsOfExperience").GetString());
        Assert.Null(data.GetProperty("primaryResume").GetString());
        Assert.Null(data.GetProperty("activeCareerGoal").GetString());
        Assert.Empty(data.GetProperty("skillProfileSummary").GetProperty("topCompetencies").EnumerateArray());
        Assert.Empty(data.GetProperty("skillProfileSummary").GetProperty("topWeaknessSignals").EnumerateArray());
        Assert.Null(data.GetProperty("learningPath").GetString());
        Assert.True(data.GetProperty("onboarding").GetProperty("hasDisplayName").GetBoolean());
        Assert.False(data.GetProperty("onboarding").GetProperty("hasYearsOfExperience").GetBoolean());
        Assert.False(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        Assert.False(data.GetProperty("onboarding").GetProperty("hasActiveCareerGoal").GetBoolean());
        Assert.False(data.GetProperty("onboarding").GetProperty("isComplete").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await db.LearningPaths.CountAsync(item => item.UserId == account.UserId));
        Assert.Null(await db.UserProfiles.Where(item => item.UserId == account.UserId)
            .Select(item => item.YearsOfExperience).SingleAsync());
    }

    [Fact]
    public async Task ProfilePatchUpdatesOnlySuppliedFieldsAndUsesAuthenticatedOwner()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "Profile owner");
        var other = await RegisterAsync(otherClient, "Other profile owner");
        Authorize(ownerClient, owner);
        Authorize(otherClient, other);

        using var displayUpdate = await ownerClient.PatchAsJsonAsync("/api/v1/me/profile", new
        {
            displayName = "  Updated profile owner  ",
            email = "attacker@example.test",
            userId = other.UserId
        });
        var displayData = await DataAsync(displayUpdate);
        Assert.Equal(owner.UserId, displayData.GetProperty("id").GetGuid());
        Assert.Equal(owner.Email, displayData.GetProperty("email").GetString());
        Assert.Equal("Updated profile owner", displayData.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, displayData.GetProperty("yearsOfExperience").ValueKind);
        Assert.Equal(JsonValueKind.Array, displayData.GetProperty("roles").ValueKind);
        Assert.Equal(JsonValueKind.Null, displayData.GetProperty("billing").ValueKind);

        using var yearsUpdate = await ownerClient.PatchAsJsonAsync(
            "/api/v1/me/profile", new { yearsOfExperience = 2 });
        var yearsData = await DataAsync(yearsUpdate);
        Assert.Equal(owner.UserId, yearsData.GetProperty("id").GetGuid());
        Assert.Equal(owner.Email, yearsData.GetProperty("email").GetString());
        Assert.Equal("Updated profile owner", yearsData.GetProperty("displayName").GetString());
        Assert.Equal(2, yearsData.GetProperty("yearsOfExperience").GetInt32());
        Assert.Equal(JsonValueKind.Array, yearsData.GetProperty("roles").ValueKind);
        Assert.Equal(JsonValueKind.Null, yearsData.GetProperty("billing").ValueKind);

        using var otherProfile = await otherClient.GetAsync("/api/v1/me/career-profile");
        var otherData = await DataAsync(otherProfile);
        Assert.Equal(other.UserId, otherData.GetProperty("profile").GetProperty("userId").GetGuid());
        Assert.Equal("Other profile owner", otherData.GetProperty("profile").GetProperty("displayName").GetString());
        Assert.Null(otherData.GetProperty("profile").GetProperty("yearsOfExperience").GetString());
    }

    [Fact]
    public async Task ProfilePatchRejectsInvalidValues()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "Profile validation candidate");
        Authorize(client, account);

        using var negativeYears = await client.PatchAsJsonAsync(
            "/api/v1/me/profile", new { yearsOfExperience = -1 });
        Assert.Equal(HttpStatusCode.BadRequest, negativeYears.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ErrorCodeAsync(negativeYears));

        using var excessiveYears = await client.PatchAsJsonAsync(
            "/api/v1/me/profile", new { yearsOfExperience = 61 });
        Assert.Equal(HttpStatusCode.BadRequest, excessiveYears.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ErrorCodeAsync(excessiveYears));

        using var blankDisplayName = await client.PatchAsJsonAsync(
            "/api/v1/me/profile", new { displayName = "  " });
        Assert.Equal(HttpStatusCode.BadRequest, blankDisplayName.StatusCode);
        Assert.Equal("DISPLAY_NAME_REQUIRED", await ErrorCodeAsync(blankDisplayName));

        using var oversizedDisplayName = await client.PatchAsJsonAsync(
            "/api/v1/me/profile", new { displayName = new string('x', 121) });
        Assert.Equal(HttpStatusCode.BadRequest, oversizedDisplayName.StatusCode);
        Assert.Equal("VALIDATION_ERROR", await ErrorCodeAsync(oversizedDisplayName));
    }

    [Fact]
    public async Task OnboardingCompletesWithOnlyProfilePrimaryResumeAndActiveGoal()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "Minimal setup candidate");
        Authorize(client, account);

        using var profileUpdate = await client.PatchAsJsonAsync(
            "/api/v1/me/profile", new { yearsOfExperience = 0 });
        Assert.Equal(HttpStatusCode.OK, profileUpdate.StatusCode);

        var resumeId = await SeedResumeAsync(factory, account.UserId, PracticeValues.Ready, At(1), "primary.pdf");
        using var selection = await client.PutAsJsonAsync(
            "/api/v1/me/primary-resume", new { resumeId });
        Assert.Equal(HttpStatusCode.OK, selection.StatusCode);

        using var goalResponse = await client.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = "Backend Developer",
            seniority = "mid"
        });
        Assert.Equal(HttpStatusCode.Created, goalResponse.StatusCode);

        using var response = await client.GetAsync("/api/v1/me/career-profile");
        var data = await DataAsync(response);
        var onboarding = data.GetProperty("onboarding");
        Assert.True(onboarding.GetProperty("hasDisplayName").GetBoolean());
        Assert.True(onboarding.GetProperty("hasYearsOfExperience").GetBoolean());
        Assert.True(onboarding.GetProperty("hasPrimaryResume").GetBoolean());
        Assert.True(onboarding.GetProperty("hasActiveCareerGoal").GetBoolean());
        Assert.True(onboarding.GetProperty("isComplete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("learningPath").ValueKind);
        Assert.Empty(data.GetProperty("skillProfileSummary").GetProperty("topCompetencies").EnumerateArray());
        Assert.Empty(data.GetProperty("skillProfileSummary").GetProperty("topWeaknessSignals").EnumerateArray());
    }

    [Fact]
    public async Task ResumeListIsOwnerScopedNewestFirstAndOmitsPrivateFields()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "Resume list owner");
        var other = await RegisterAsync(otherClient, "Resume list other");
        Authorize(ownerClient, owner);
        Authorize(otherClient, other);

        var oldestId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(1), "oldest.pdf");
        var tiedLowerId = await SeedResumeAsync(
            factory, owner.UserId, PracticeValues.Ready, At(2), "tied-lower.pdf",
            Guid.Parse("10000000-0000-0000-0000-000000000001"));
        var tiedHigherId = await SeedResumeAsync(
            factory, owner.UserId, PracticeValues.Failed, At(2), "tied-higher.pdf",
            Guid.Parse("10000000-0000-0000-0000-000000000002"));
        var foreignId = await SeedResumeAsync(factory, other.UserId, PracticeValues.Ready, At(3), "foreign.pdf");

        using var response = await ownerClient.GetAsync("/api/v1/resumes");
        var resumes = await DataAsync(response);
        var items = resumes.EnumerateArray().ToArray();

        Assert.Equal(3, items.Length);
        Assert.Equal(tiedHigherId, items[0].GetProperty("id").GetGuid());
        Assert.Equal(tiedLowerId, items[1].GetProperty("id").GetGuid());
        Assert.Equal(oldestId, items[2].GetProperty("id").GetGuid());
        Assert.DoesNotContain(foreignId, items.Select(item => item.GetProperty("id").GetGuid()));

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("extractedText", body, StringComparison.Ordinal);
        Assert.DoesNotContain("structuredProfile", body, StringComparison.Ordinal);
        Assert.DoesNotContain("storageKey", body, StringComparison.Ordinal);
        Assert.Equal("RESUME_EXTRACTION_FAILED", items[0].GetProperty("errorCode").GetString());

        using var emptyClient = factory.CreateHttpsClient();
        var empty = await RegisterAsync(emptyClient, "Empty resume list");
        Authorize(emptyClient, empty);
        using var emptyResponse = await emptyClient.GetAsync("/api/v1/resumes");
        Assert.Empty((await DataAsync(emptyResponse)).EnumerateArray());
    }

    [Fact]
    public async Task ResumeDeleteIsOwnerScopedIdempotentAndPreservesHistory()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "Resume deletion owner");
        var other = await RegisterAsync(otherClient, "Resume deletion other");
        Authorize(ownerClient, owner);
        Authorize(otherClient, other);

        var primaryResumeId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(1), "primary.pdf");
        var deletedResumeId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(2), "deleted.pdf");
        var foreignResumeId = await SeedResumeAsync(factory, other.UserId, PracticeValues.Ready, At(3), "foreign.pdf");
        var historicalAnalysisId = await SeedResumeAnalysisAsync(factory, owner.UserId, deletedResumeId, At(4));
        using (var selection = await ownerClient.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = primaryResumeId }))
            Assert.Equal(HttpStatusCode.OK, selection.StatusCode);

        using var foreignDelete = await ownerClient.DeleteAsync($"/api/v1/resumes/{foreignResumeId}");
        using var unknownDelete = await ownerClient.DeleteAsync($"/api/v1/resumes/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, foreignDelete.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownDelete.StatusCode);

        var concurrentDeletes = await Task.WhenAll(
            ownerClient.DeleteAsync($"/api/v1/resumes/{deletedResumeId}"),
            ownerClient.DeleteAsync($"/api/v1/resumes/{deletedResumeId}"));
        using (concurrentDeletes[0])
            Assert.Equal(HttpStatusCode.NoContent, concurrentDeletes[0].StatusCode);
        using (concurrentDeletes[1])
            Assert.Equal(HttpStatusCode.NoContent, concurrentDeletes[1].StatusCode);
        using (var replay = await ownerClient.DeleteAsync($"/api/v1/resumes/{deletedResumeId}"))
            Assert.Equal(HttpStatusCode.NoContent, replay.StatusCode);
        using (var selectDeleted = await ownerClient.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = deletedResumeId }))
            Assert.Equal(HttpStatusCode.NotFound, selectDeleted.StatusCode);

        using (var analysisRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/resume-analyses")
        {
            Content = JsonContent.Create(new
            {
                resumeId = deletedResumeId,
                mode = ResumeAnalysisModes.FieldBenchmark,
                industry = "Fintech",
                targetRole = "Backend Engineer",
                seniority = "senior"
            })
        })
        {
            analysisRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await ownerClient.SendAsync(analysisRequest);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
        using (var interviewRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new
            {
                role = "Backend developer", seniority = "junior", interviewType = "technical",
                difficulty = "medium", resumeId = deletedResumeId
            })
        })
        {
            interviewRequest.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
            using var response = await ownerClient.SendAsync(interviewRequest);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using (var careerProfile = await ownerClient.GetAsync("/api/v1/me/career-profile"))
        {
            var data = await DataAsync(careerProfile);
            Assert.Equal(primaryResumeId, data.GetProperty("primaryResume").GetProperty("id").GetGuid());
        }
        using (var profile = await ownerClient.GetAsync("/api/v1/skill-profile"))
        {
            var data = await DataAsync(profile);
            Assert.Empty(data.GetProperty("competencies").EnumerateArray());
            Assert.Empty(data.GetProperty("weaknessSignals").EnumerateArray());
        }
        using (var deletedRead = await ownerClient.GetAsync($"/api/v1/resumes/{deletedResumeId}"))
            Assert.Equal(HttpStatusCode.NotFound, deletedRead.StatusCode);
        using (var resumeList = await ownerClient.GetAsync("/api/v1/resumes"))
            Assert.DoesNotContain(deletedResumeId,
                (await DataAsync(resumeList)).EnumerateArray().Select(item => item.GetProperty("id").GetGuid()));
        using (var foreignRead = await ownerClient.GetAsync($"/api/v1/resumes/{foreignResumeId}"))
            Assert.Equal(HttpStatusCode.NotFound, foreignRead.StatusCode);
        using (var otherRead = await otherClient.GetAsync($"/api/v1/resumes/{foreignResumeId}"))
            Assert.Equal(HttpStatusCode.OK, otherRead.StatusCode);

        using (var deletePrimary = await ownerClient.DeleteAsync($"/api/v1/resumes/{primaryResumeId}"))
            Assert.Equal(HttpStatusCode.NoContent, deletePrimary.StatusCode);
        using (var careerProfile = await ownerClient.GetAsync("/api/v1/me/career-profile"))
        {
            var data = await DataAsync(careerProfile);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("primaryResume").ValueKind);
            Assert.False(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.NotNull(await db.Resumes.Where(item => item.Id == deletedResumeId).Select(item => item.DeletedAt).SingleAsync());
        Assert.NotNull(await db.Resumes.Where(item => item.Id == primaryResumeId).Select(item => item.DeletedAt).SingleAsync());
        Assert.Equal(historicalAnalysisId, await db.ResumeAnalyses.Where(item => item.Id == historicalAnalysisId)
            .Select(item => item.Id).SingleAsync());
        Assert.Null(await db.UserProfiles.Where(item => item.UserId == owner.UserId)
            .Select(item => item.PrimaryResumeId).SingleAsync());
        Assert.Equal("deleted", await db.RealtimeNotifications
            .Where(item => item.UserId == owner.UserId && item.ResourceType == "resume" && item.ResourceId == primaryResumeId)
            .Select(item => item.Status).SingleAsync());
    }

    [Fact]
    public async Task PrimaryResumeIsOwnerScopedIdempotentAndNotAutoSelected()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "Primary resume owner");
        var other = await RegisterAsync(otherClient, "Other resume owner");
        Authorize(ownerClient, owner);
        Authorize(otherClient, other);

        var firstResumeId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(1), "first.pdf");
        var secondResumeId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(2), "second.pdf");
        var unavailableResumeId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Uploaded, At(3), "pending.pdf");
        var deletableResumeId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(3), "deletable.pdf");
        var foreignResumeId = await SeedResumeAsync(factory, other.UserId, PracticeValues.Ready, At(4), "foreign.pdf");
        var secondAnalysisId = await SeedResumeAnalysisAsync(factory, owner.UserId, secondResumeId, At(5));

        using (var beforeSelection = await ownerClient.GetAsync("/api/v1/me/career-profile"))
        {
            var data = await DataAsync(beforeSelection);
            Assert.Null(data.GetProperty("primaryResume").GetString());
            Assert.False(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        }

        using var firstSelection = await ownerClient.PutAsJsonAsync(
            "/api/v1/me/primary-resume", new { resumeId = firstResumeId });
        var firstSelectionData = await DataAsync(firstSelection);
        Assert.Equal(firstResumeId, firstSelectionData.GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Null, firstSelectionData.GetProperty("latestAnalysis").ValueKind);

        using (var zeroAnalysisProfile = await ownerClient.GetAsync("/api/v1/me/career-profile"))
        {
            var data = await DataAsync(zeroAnalysisProfile);
            Assert.Equal(firstResumeId, data.GetProperty("primaryResume").GetProperty("id").GetGuid());
            Assert.Equal(JsonValueKind.Null,
                data.GetProperty("primaryResume").GetProperty("latestAnalysis").ValueKind);
        }

        using var repeatedSelection = await ownerClient.PutAsJsonAsync(
            "/api/v1/me/primary-resume", new { resumeId = firstResumeId });
        Assert.Equal(HttpStatusCode.OK, repeatedSelection.StatusCode);
        Assert.Equal(firstResumeId, (await DataAsync(repeatedSelection)).GetProperty("id").GetGuid());

        using var replacement = await ownerClient.PutAsJsonAsync(
            "/api/v1/me/primary-resume", new { resumeId = secondResumeId });
        Assert.Equal(HttpStatusCode.OK, replacement.StatusCode);

        using (var profile = await ownerClient.GetAsync("/api/v1/me/career-profile"))
        {
            var data = await DataAsync(profile);
            Assert.Equal(secondResumeId, data.GetProperty("primaryResume").GetProperty("id").GetGuid());
            Assert.Equal(secondAnalysisId, data.GetProperty("primaryResume").GetProperty("latestAnalysis").GetProperty("id").GetGuid());
        }

        using (var clear = await ownerClient.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = (Guid?)null }))
        {
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            using var document = JsonDocument.Parse(await clear.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
        }

        using (var afterClear = await ownerClient.GetAsync("/api/v1/me/career-profile"))
        {
            var data = await DataAsync(afterClear);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("primaryResume").ValueKind);
            Assert.False(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        }

        using (var repeatedClear = await ownerClient.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = (Guid?)null }))
        {
            Assert.Equal(HttpStatusCode.OK, repeatedClear.StatusCode);
            using var document = JsonDocument.Parse(await repeatedClear.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("data").ValueKind);
        }

        using (var otherProfile = await otherClient.GetAsync("/api/v1/me/career-profile"))
        {
            var data = await DataAsync(otherProfile);
            Assert.Equal(other.UserId, data.GetProperty("profile").GetProperty("userId").GetGuid());
            Assert.Null(data.GetProperty("primaryResume").GetString());
        }

        using (var foreign = await ownerClient.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = foreignResumeId }))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        using (var missing = await ownerClient.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = Guid.NewGuid() }))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using (var unavailable = await ownerClient.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = unavailableResumeId }))
        {
            Assert.Equal(HttpStatusCode.Conflict, unavailable.StatusCode);
            Assert.Equal("RESUME_NOT_READY", await ErrorCodeAsync(unavailable));
        }

        using var selectDeletable = await ownerClient.PutAsJsonAsync(
            "/api/v1/me/primary-resume", new { resumeId = deletableResumeId });
        Assert.Equal(HttpStatusCode.OK, selectDeletable.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.Resumes.Remove(await db.Resumes.SingleAsync(item => item.Id == deletableResumeId));
            await db.SaveChangesAsync();
        }
        using (var afterDelete = await ownerClient.GetAsync("/api/v1/me/career-profile"))
        {
            var data = await DataAsync(afterDelete);
            Assert.Null(data.GetProperty("primaryResume").GetString());
            Assert.False(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        }

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var profileRow = await verificationDb.UserProfiles.AsNoTracking().SingleAsync(item => item.UserId == owner.UserId);
        Assert.Null(profileRow.PrimaryResumeId);
    }

    [Fact]
    public async Task LatestPrimaryResumeAnalysisUsesCreatedAtThenIdAndOwnerResumeScope()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "Analysis ordering owner");
        var other = await RegisterAsync(otherClient, "Analysis ordering other");
        Authorize(ownerClient, owner);

        var selectedResumeId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(1), "selected.pdf");
        var otherResumeId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(2), "other.pdf");
        var foreignResumeId = await SeedResumeAsync(factory, other.UserId, PracticeValues.Ready, At(3), "foreign.pdf");
        await SeedResumeAnalysisAsync(factory, owner.UserId, selectedResumeId, At(4), analysisId: Guid.Parse("10000000-0000-0000-0000-000000000001"));
        await SeedResumeAnalysisAsync(factory, owner.UserId, otherResumeId, At(9));
        await SeedResumeAnalysisAsync(factory, other.UserId, foreignResumeId, At(10));
        await SeedResumeAnalysisAsync(factory, owner.UserId, selectedResumeId, At(5), analysisId: Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var expectedId = await SeedResumeAnalysisAsync(
            factory,
            owner.UserId,
            selectedResumeId,
            At(5),
            analysisId: Guid.Parse("20000000-0000-0000-0000-000000000002"));

        using var selection = await ownerClient.PutAsJsonAsync(
            "/api/v1/me/primary-resume", new { resumeId = selectedResumeId });
        var selectionData = await DataAsync(selection);
        Assert.Equal(expectedId, selectionData.GetProperty("latestAnalysis").GetProperty("id").GetGuid());

        using var profile = await ownerClient.GetAsync("/api/v1/me/career-profile");
        var profileData = await DataAsync(profile);
        Assert.Equal(expectedId,
            profileData.GetProperty("primaryResume").GetProperty("latestAnalysis").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task RejectedPrimaryResumeSelectionsPreserveExistingSelection()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "Rejected selection owner");
        var other = await RegisterAsync(otherClient, "Rejected selection other");
        Authorize(ownerClient, owner);

        var currentId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(1), "current.pdf");
        var pendingId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Uploaded, At(2), "pending.pdf");
        var deletedId = await SeedResumeAsync(factory, owner.UserId, PracticeValues.Ready, At(3), "deleted.pdf");
        var foreignId = await SeedResumeAsync(factory, other.UserId, PracticeValues.Ready, At(4), "foreign.pdf");
        await MarkResumeDeletedAsync(factory, deletedId, At(5));
        using (var selected = await ownerClient.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = currentId }))
            Assert.Equal(HttpStatusCode.OK, selected.StatusCode);

        var rejectedIds = new[] { Guid.NewGuid(), foreignId, deletedId, pendingId };
        foreach (var rejectedId in rejectedIds)
        {
            using var response = await ownerClient.PutAsJsonAsync(
                "/api/v1/me/primary-resume", new { resumeId = rejectedId });
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict);
            Assert.Equal(currentId, await GetPersistedPrimaryResumeIdAsync(factory, owner.UserId));
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("deleted")]
    [InlineData("not-ready")]
    public async Task StalePrimaryResumeReferenceDegradesToNullWithoutMutation(string staleKind)
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, $"Stale primary {staleKind}");
        Authorize(client, account);

        var staleId = Guid.NewGuid();
        if (staleKind != "missing")
        {
            staleId = await SeedResumeAsync(
                factory,
                account.UserId,
                staleKind == "deleted" ? PracticeValues.Ready : PracticeValues.Uploaded,
                At(1),
                $"{staleKind}.pdf");
            if (staleKind == "deleted") await MarkResumeDeletedAsync(factory, staleId, At(2));
        }
        await SetStalePrimaryResumeIdAsync(factory, account.UserId, staleId);

        using var response = await client.GetAsync("/api/v1/me/career-profile");
        var data = await DataAsync(response);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("primaryResume").ValueKind);
        Assert.False(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        Assert.Equal(staleId, await GetPersistedPrimaryResumeIdAsync(factory, account.UserId));
    }

    [Fact]
    public async Task ResponseSummaryFailureRollsBackPrimaryResumeChange()
    {
        var interceptor = new FailLatestAnalysisReadInterceptor();
        using var factory = new NexoraApiFactory(interceptor);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "Primary resume rollback candidate");
        Authorize(client, account);
        var originalId = await SeedResumeAsync(factory, account.UserId, PracticeValues.Ready, At(1), "original.pdf");
        var replacementId = await SeedResumeAsync(factory, account.UserId, PracticeValues.Ready, At(2), "replacement.pdf");
        using (var original = await client.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = originalId }))
            Assert.Equal(HttpStatusCode.OK, original.StatusCode);

        interceptor.Arm();
        using var failed = await client.PutAsJsonAsync(
            "/api/v1/me/primary-resume", new { resumeId = replacementId });

        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        Assert.Equal(originalId, await GetPersistedPrimaryResumeIdAsync(factory, account.UserId));
    }

    [Fact]
    public async Task CareerProfileAggregatesPrimaryResumeGoalSkillSummaryAndPath()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "Full career profile candidate");
        Authorize(client, account);

        var primaryResumeId = await SeedResumeAsync(factory, account.UserId, PracticeValues.Ready, At(1), "primary.pdf");
        var historicalResumeId = await SeedResumeAsync(factory, account.UserId, PracticeValues.Ready, At(2), "historical.pdf");
        await SeedResumeAnalysisAsync(factory, account.UserId, primaryResumeId, At(3), scoreOffset: 0);
        var latestPrimaryAnalysisId = await SeedResumeAnalysisAsync(factory, account.UserId, primaryResumeId, At(4), scoreOffset: 5);
        await SeedResumeAnalysisAsync(factory, account.UserId, historicalResumeId, At(5), scoreOffset: 25);

        using var goalResponse = await client.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = "Backend Developer",
            seniority = "senior",
            industry = "Fintech",
            targetCompany = "Nexora"
        });
        Assert.Equal(HttpStatusCode.Created, goalResponse.StatusCode);
        var goalId = (await DataAsync(goalResponse)).GetProperty("id").GetGuid();
        await SeedLearningPathAsync(factory, account.UserId, goalId, At(6));

        using var selection = await client.PutAsJsonAsync(
            "/api/v1/me/primary-resume", new { resumeId = primaryResumeId });
        Assert.Equal(HttpStatusCode.OK, selection.StatusCode);

        using var profileUpdate = await client.PatchAsJsonAsync(
            "/api/v1/me/profile", new { yearsOfExperience = 6 });
        var profileData = await DataAsync(profileUpdate);
        Assert.Equal(6, profileData.GetProperty("yearsOfExperience").GetInt32());

        using var response = await client.GetAsync("/api/v1/me/career-profile");
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("extractedText", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain("storageKey", responseBody, StringComparison.Ordinal);
        var data = await DataAsync(response);
        var primaryResume = data.GetProperty("primaryResume");
        var goal = data.GetProperty("activeCareerGoal");
        var skills = data.GetProperty("skillProfileSummary");
        var learningPath = data.GetProperty("learningPath");

        Assert.Equal(account.UserId, data.GetProperty("profile").GetProperty("userId").GetGuid());
        Assert.Equal("Full career profile candidate", data.GetProperty("profile").GetProperty("displayName").GetString());
        Assert.Equal(6, data.GetProperty("profile").GetProperty("yearsOfExperience").GetInt32());
        Assert.Equal(primaryResumeId, primaryResume.GetProperty("id").GetGuid());
        Assert.Equal(latestPrimaryAnalysisId, primaryResume.GetProperty("latestAnalysis").GetProperty("id").GetGuid());
        Assert.Equal("field_benchmark", primaryResume.GetProperty("latestAnalysis").GetProperty("mode").GetString());
        Assert.Equal(goalId, goal.GetProperty("id").GetGuid());
        Assert.Equal("Backend Developer", goal.GetProperty("targetRole").GetString());
        Assert.True(goal.GetProperty("active").GetBoolean());

        var competencies = skills.GetProperty("topCompetencies").EnumerateArray().ToArray();
        Assert.Equal(5, competencies.Length);
        Assert.Equal("resume.clarity", competencies[0].GetProperty("code").GetString());
        Assert.Equal("resume.impact_achievements", competencies[1].GetProperty("code").GetString());
        Assert.All(competencies, item => Assert.True(item.GetProperty("evidenceCount").GetInt32() >= 1));
        var weaknesses = skills.GetProperty("topWeaknessSignals").EnumerateArray().ToArray();
        Assert.NotEmpty(weaknesses);
        Assert.Equal(SkillProfileSourceTypes.ResumeAnalysis, weaknesses[0].GetProperty("sourceType").GetString());

        Assert.Equal("active", learningPath.GetProperty("status").GetString());
        Assert.Equal(1, learningPath.GetProperty("pendingActivityCount").GetInt32());
        Assert.Equal(1, learningPath.GetProperty("completedActivityCount").GetInt32());
        Assert.True(data.GetProperty("onboarding").GetProperty("hasDisplayName").GetBoolean());
        Assert.True(data.GetProperty("onboarding").GetProperty("hasYearsOfExperience").GetBoolean());
        Assert.True(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        Assert.True(data.GetProperty("onboarding").GetProperty("hasActiveCareerGoal").GetBoolean());
        Assert.True(data.GetProperty("onboarding").GetProperty("isComplete").GetBoolean());

        using (var deactivateGoal = await client.PatchAsJsonAsync(
                   $"/api/v1/career-goals/{goalId}", new { active = false }))
        {
            Assert.Equal(HttpStatusCode.OK, deactivateGoal.StatusCode);
        }

        using (var afterDeactivate = await client.GetAsync("/api/v1/me/career-profile"))
        {
            var deactivated = await DataAsync(afterDeactivate);
            Assert.Equal(JsonValueKind.Null, deactivated.GetProperty("activeCareerGoal").ValueKind);
            Assert.True(deactivated.GetProperty("onboarding").GetProperty("hasDisplayName").GetBoolean());
            Assert.True(deactivated.GetProperty("onboarding").GetProperty("hasYearsOfExperience").GetBoolean());
            Assert.True(deactivated.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
            Assert.False(deactivated.GetProperty("onboarding").GetProperty("hasActiveCareerGoal").GetBoolean());
            Assert.False(deactivated.GetProperty("onboarding").GetProperty("isComplete").GetBoolean());
        }

        using (var reactivateGoal = await client.PatchAsJsonAsync(
                   $"/api/v1/career-goals/{goalId}", new { active = true }))
        {
            Assert.Equal(HttpStatusCode.OK, reactivateGoal.StatusCode);
        }

        using (var clear = await client.PutAsJsonAsync(
                   "/api/v1/me/primary-resume", new { resumeId = (Guid?)null }))
        {
            Assert.Equal(HttpStatusCode.OK, clear.StatusCode);
            using var clearDocument = JsonDocument.Parse(await clear.Content.ReadAsStringAsync());
            Assert.Equal(JsonValueKind.Null, clearDocument.RootElement.GetProperty("data").ValueKind);
        }

        using (var afterClear = await client.GetAsync("/api/v1/me/career-profile"))
        {
            var cleared = await DataAsync(afterClear);
            Assert.Equal(JsonValueKind.Null, cleared.GetProperty("primaryResume").ValueKind);
            Assert.Equal(goalId, cleared.GetProperty("activeCareerGoal").GetProperty("id").GetGuid());
            Assert.Equal("active", cleared.GetProperty("learningPath").GetProperty("status").GetString());
            Assert.True(cleared.GetProperty("onboarding").GetProperty("hasDisplayName").GetBoolean());
            Assert.True(cleared.GetProperty("onboarding").GetProperty("hasYearsOfExperience").GetBoolean());
            Assert.True(cleared.GetProperty("onboarding").GetProperty("hasActiveCareerGoal").GetBoolean());
            Assert.False(cleared.GetProperty("onboarding").GetProperty("isComplete").GetBoolean());
            Assert.Equal(competencies.Length, cleared.GetProperty("skillProfileSummary").GetProperty("topCompetencies").GetArrayLength());
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.LearningPaths.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(2, await db.LearningPathActivities.CountAsync(item => item.LearningPath.UserId == account.UserId));
    }

    private static void Authorize(HttpClient client, Account account) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

    private static async Task<Account> RegisterAsync(HttpClient client, string displayName)
    {
        var email = $"career-profile-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return new Account(
            data.GetProperty("user").GetProperty("id").GetGuid(),
            data.GetProperty("accessToken").GetString()!,
            email);
    }

    private static async Task<Guid> SeedResumeAsync(
        NexoraApiFactory factory,
        Guid userId,
        string status,
        DateTimeOffset createdAt,
        string fileName,
        Guid? resumeId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = $"career-profile/{Guid.NewGuid():N}",
            FileName = fileName,
            ContentType = "application/pdf",
            Size = 100,
            Checksum = Guid.NewGuid().ToString("N"),
            CreatedAt = createdAt
        };
        var resume = new ResumeRecord
        {
            Id = resumeId ?? Guid.NewGuid(),
            UserId = userId,
            StoredFileId = file.Id,
            Status = status,
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        db.AddRange(file, resume);
        await db.SaveChangesAsync();
        return resume.Id;
    }

    private static async Task<Guid> SeedResumeAnalysisAsync(
        NexoraApiFactory factory,
        Guid userId,
        Guid resumeId,
        DateTimeOffset createdAt,
        int scoreOffset = 0,
        Guid? analysisId = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var result = new ResumeAnalysisOutput(
            ["strength"],
            [$"gap-{createdAt:dd}"],
            ["recommendation"],
            ReadinessScore: 70,
            Summary: "summary",
            SectionFeedback: ["section"],
            Breakdown: new Dictionary<string, int>
            {
                ["technicalFoundation"] = 60 + scoreOffset,
                ["projectEvidence"] = 80 + scoreOffset,
                ["experiencePresentation"] = 70 + scoreOffset,
                ["impactAchievements"] = 85 + scoreOffset,
                ["clarity"] = 90 + scoreOffset,
                ["roleAlignment"] = 50 + scoreOffset
            },
            Mode: ResumeAnalysisModes.FieldBenchmark);
        var analysis = new ResumeAnalysis
        {
            Id = analysisId ?? Guid.NewGuid(),
            UserId = userId,
            ResumeId = resumeId,
            Mode = ResumeAnalysisModes.FieldBenchmark,
            Status = PracticeValues.Completed,
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            Result = JsonSerializer.Serialize(result, JsonOptions),
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            CompletedAt = createdAt
        };
        db.ResumeAnalyses.Add(analysis);
        await db.SaveChangesAsync();
        return analysis.Id;
    }

    private static async Task MarkResumeDeletedAsync(
        NexoraApiFactory factory,
        Guid resumeId,
        DateTimeOffset deletedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var resume = await db.Resumes.SingleAsync(item => item.Id == resumeId);
        resume.DeletedAt = deletedAt;
        resume.UpdatedAt = deletedAt;
        await db.SaveChangesAsync();
    }

    private static async Task SetStalePrimaryResumeIdAsync(
        NexoraApiFactory factory,
        Guid userId,
        Guid resumeId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF;");
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE user_profiles SET PrimaryResumeId = {resumeId} WHERE UserId = {userId}");
            await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task<Guid?> GetPersistedPrimaryResumeIdAsync(
        NexoraApiFactory factory,
        Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        return await db.UserProfiles.AsNoTracking()
            .Where(item => item.UserId == userId)
            .Select(item => item.PrimaryResumeId)
            .SingleAsync();
    }

    private sealed class FailLatestAnalysisReadInterceptor : DbCommandInterceptor
    {
        private bool _armed;

        public void Arm() => _armed = true;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_armed && command.CommandText.Contains("FROM \"resume_analyses\"", StringComparison.Ordinal))
            {
                _armed = false;
                return ValueTask.FromException<InterceptionResult<DbDataReader>>(
                    new InvalidOperationException("Injected latest-analysis read failure."));
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task SeedLearningPathAsync(
        NexoraApiFactory factory,
        Guid userId,
        Guid careerGoalId,
        DateTimeOffset createdAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var path = new LearningPath
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CareerGoalId = careerGoalId,
            Status = LearningPathValues.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        var milestone = new LearningPathMilestone
        {
            Id = Guid.NewGuid(),
            LearningPathId = path.Id,
            Code = LearningPathValues.CriticalMilestone,
            Title = "Critical gaps",
            SortOrder = 0,
            Status = LearningPathValues.Active,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        var pending = NewActivity(path, milestone, "pending", LearningPathValues.Pending, createdAt);
        var completed = NewActivity(path, milestone, "completed", LearningPathValues.Completed, createdAt);
        completed.CompletedAt = createdAt;
        db.AddRange(path, milestone, pending, completed);
        await db.SaveChangesAsync();
    }

    private static LearningPathActivity NewActivity(
        LearningPath path,
        LearningPathMilestone milestone,
        string key,
        string status,
        DateTimeOffset createdAt) => new()
        {
            Id = Guid.NewGuid(),
            LearningPathId = path.Id,
            LearningPathMilestoneId = milestone.Id,
            ActivityKey = $"career-profile:{key}",
            Type = LearningPathValues.Interview,
            Title = $"{key} activity",
            Description = "Career profile test activity",
            Priority = 1,
            SortOrder = key == "pending" ? 0 : 1,
            Status = status,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode,
            $"Expected success, got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static DateTimeOffset At(int day) => new(2026, 9, day, 0, 0, 0, TimeSpan.Zero);

    private sealed record Account(Guid UserId, string AccessToken, string Email);
}
