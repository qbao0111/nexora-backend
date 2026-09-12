using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

        Assert.Null(data.GetProperty("primaryResume").GetString());
        Assert.Null(data.GetProperty("activeCareerGoal").GetString());
        Assert.Empty(data.GetProperty("skillProfileSummary").GetProperty("topCompetencies").EnumerateArray());
        Assert.Empty(data.GetProperty("skillProfileSummary").GetProperty("topWeaknessSignals").EnumerateArray());
        Assert.Null(data.GetProperty("learningPath").GetString());
        Assert.False(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        Assert.False(data.GetProperty("onboarding").GetProperty("hasActiveCareerGoal").GetBoolean());
        Assert.False(data.GetProperty("onboarding").GetProperty("isComplete").GetBoolean());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await db.LearningPaths.CountAsync(item => item.UserId == account.UserId));
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
        Assert.True(data.GetProperty("onboarding").GetProperty("hasPrimaryResume").GetBoolean());
        Assert.True(data.GetProperty("onboarding").GetProperty("hasActiveCareerGoal").GetBoolean());
        Assert.True(data.GetProperty("onboarding").GetProperty("isComplete").GetBoolean());

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
            data.GetProperty("accessToken").GetString()!);
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
        int scoreOffset = 0)
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
            Id = Guid.NewGuid(),
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

    private sealed record Account(Guid UserId, string AccessToken);
}
