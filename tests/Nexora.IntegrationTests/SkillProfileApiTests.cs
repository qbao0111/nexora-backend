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

public sealed class SkillProfileApiTests
{
    [Fact]
    public async Task SkillProfileRequiresAuthentication()
    {
        using var factory = NewFactory();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/v1/skill-profile");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedUserWithNoEvidenceGetsAnEmptyProfile()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);

        using var response = await client.GetAsync("/api/v1/skill-profile");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        Assert.Empty(data.GetProperty("competencies").EnumerateArray());
        Assert.Empty(data.GetProperty("weaknessSignals").EnumerateArray());
    }

    [Fact]
    public async Task CompletedCvBreakdownContributesAndGapRemainsQualitative()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var at = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed, ValidResumeOutput(
            clarity: 73,
            gaps: ["Needs API design evidence"],
            missing: ["SQL"]), at);

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);

        var clarity = Assert.Single(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "resume.clarity");
        Assert.Equal(73, clarity.GetProperty("score").GetInt32());
        Assert.Equal(1, clarity.GetProperty("evidenceCount").GetInt32());
        Assert.Contains(data.GetProperty("weaknessSignals").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "Needs API design evidence");
        Assert.Contains(data.GetProperty("weaknessSignals").EnumerateArray(),
            item => item.GetProperty("label").GetString() == "SQL");
        Assert.DoesNotContain(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "resume.sql");
    }

    [Fact]
    public async Task OnlyLatestValidCvAnalysisContributesWeaknessSignals()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var oldAt = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(70, ["SQL", "Agile"], []), oldAt);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(80, ["API integration"], []), oldAt.AddDays(1));

        var labels = await WeaknessLabelsAsync(client);

        Assert.Equal(["API integration"], labels);
    }

    [Fact]
    public async Task LatestValidCvAnalysisDeduplicatesDuplicateLabels()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(80, ["API", " api ", "API"], []),
            new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero));

        var labels = await WeaknessLabelsAsync(client);

        Assert.Equal(["API"], labels);
    }

    [Fact]
    public async Task LatestValidCvAnalysisDeduplicatesCaseAndWhitespace()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(80, [" SQL ", "sql"], ["Agile", " agile "]),
            new DateTimeOffset(2026, 9, 12, 8, 0, 0, TimeSpan.Zero));

        var labels = await WeaknessLabelsAsync(client);

        Assert.Equal(["Agile", "SQL"], labels);
    }

    [Fact]
    public async Task MalformedNewestCompletedCvAnalysisFallsBackToLatestValidAnalysis()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var oldAt = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(70, ["Previous weakness"], []), oldAt);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            "{not-json", oldAt.AddDays(1));

        var labels = await WeaknessLabelsAsync(client);

        Assert.Equal(["Previous weakness"], labels);
    }

    [Fact]
    public async Task SemanticallyInvalidNewestCompletedCvAnalysisFallsBackToLatestValidAnalysis()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var oldAt = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(70, ["Previous weakness"], []), oldAt);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(101, ["Invalid newest weakness"], []), oldAt.AddDays(1));

        var labels = await WeaknessLabelsAsync(client);

        Assert.Equal(["Previous weakness"], labels);
    }

    [Fact]
    public async Task NewerFailedOrIncompleteCvAnalysisDoesNotReplaceLatestValidAnalysis()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var oldAt = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(70, ["Current valid weakness"], []), oldAt);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Failed,
            ValidResumeOutput(80, ["Failed weakness"], []), oldAt.AddDays(1));
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Processing,
            ValidResumeOutput(90, ["Incomplete weakness"], []), oldAt.AddDays(2));

        var labels = await WeaknessLabelsAsync(client);

        Assert.Equal(["Current valid weakness"], labels);
    }

    [Fact]
    public async Task FailedAndIncompleteCvAnalysesAreIgnored()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Failed, ValidResumeOutput(99, ["failed gap"], ["failed keyword"]), DateTimeOffset.UtcNow);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Processing, ValidResumeOutput(88, ["processing gap"], ["processing keyword"]), DateTimeOffset.UtcNow.AddMinutes(1));

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);

        Assert.Empty(data.GetProperty("competencies").EnumerateArray());
        Assert.Empty(data.GetProperty("weaknessSignals").EnumerateArray());
    }

    [Fact]
    public async Task FinalInterviewReportContributesCanonicalRubric()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var at = new DateTimeOffset(2026, 9, 11, 9, 0, 0, TimeSpan.Zero);
        await SeedInterviewAsync(factory, account.UserId, CanonicalScores(80), includeReport: true, at: at);

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);
        var correctness = Assert.Single(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "interview.correctness");

        Assert.Equal(80, correctness.GetProperty("score").GetInt32());
        Assert.Equal(1, correctness.GetProperty("evidenceCount").GetInt32());
        Assert.Equal("interview", correctness.GetProperty("sources")[0].GetProperty("sourceType").GetString());
    }

    [Fact]
    public async Task ValidFinalReportPreventsAnswerRubricDoubleCounting()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedInterviewAsync(factory, account.UserId, CanonicalScores(90), includeReport: true,
            answerEvaluation: AnswerEvaluationWithRubric(10));

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);
        var correctness = Assert.Single(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "interview.correctness");

        Assert.Equal(90, correctness.GetProperty("score").GetInt32());
        Assert.Equal(1, correctness.GetProperty("evidenceCount").GetInt32());
    }

    [Fact]
    public async Task AnswerRubricIsUsedWhenThereIsNoFinalReport()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedInterviewAsync(factory, account.UserId, CanonicalScores(67), includeReport: false);

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);
        var correctness = Assert.Single(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "interview.correctness");

        Assert.Equal(67, correctness.GetProperty("score").GetInt32());
        Assert.Equal(1, correctness.GetProperty("evidenceCount").GetInt32());
    }

    [Fact]
    public async Task ApplicableDetectedStarComponentsContributeFromBothStorageLocations()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedInterviewAsync(factory, account.UserId, CanonicalScores(80), includeReport: false,
            answerEvaluation: AnswerEvaluationWithStar(70));
        await SeedStarAttemptAsync(factory, account.UserId, StarEvaluationWithComponents(90));

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);
        var action = Assert.Single(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "behavioral.action");

        Assert.Equal(80, action.GetProperty("score").GetInt32());
        Assert.Equal(2, action.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(2, action.GetProperty("sources").EnumerateArray().Sum(item => item.GetProperty("evidenceCount").GetInt32()));
    }

    [Fact]
    public async Task NonApplicableAndUndetectedStarComponentsDoNotCreateZeroEvidence()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedInterviewAsync(factory, account.UserId, CanonicalScores(80), includeReport: false,
            answerEvaluation: AnswerEvaluationWithStar(80, resultDetected: false));
        await SeedStarAttemptAsync(factory, account.UserId, new StarEvaluation(
            false, null, new StarComponentEvaluation(99, true, "ignored", "ignored"), null, null, null, [], [], [], AiOperations.ScoreScale));

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);

        Assert.DoesNotContain(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "behavioral.result");
        Assert.Contains(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "behavioral.situation");
    }

    [Fact]
    public async Task CompletedScenarioUsesTheServerOwnedCompetency()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedScenarioAttemptAsync(factory, account.UserId, "Problem Solving", 86, PracticeFeatureValues.Completed);

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);
        var competency = Assert.Single(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "scenario.problem_solving");

        Assert.Equal(86, competency.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task IncompleteAndFailedScenarioAttemptsAreIgnored()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedScenarioAttemptAsync(factory, account.UserId, "Problem Solving", 86, PracticeFeatureValues.Processing);
        await SeedScenarioAttemptAsync(factory, account.UserId, "Incident Response", 92, PracticeFeatureValues.Failed);

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);

        Assert.Empty(data.GetProperty("competencies").EnumerateArray());
    }

    [Fact]
    public async Task MultipleEvidenceEventsUseMeanAndExposeLatestAndSourceCounts()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var first = new DateTimeOffset(2026, 9, 10, 10, 0, 0, TimeSpan.Zero);
        var second = first.AddDays(1);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed, ValidResumeOutput(70, ["gap"], []), first);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed, ValidResumeOutput(81, ["gap"], []), second);

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);
        var clarity = Assert.Single(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "resume.clarity");

        Assert.Equal(76, clarity.GetProperty("score").GetInt32());
        Assert.Equal(2, clarity.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(second, clarity.GetProperty("latestEvidenceAt").GetDateTimeOffset());
        Assert.Equal(2, clarity.GetProperty("sources")[0].GetProperty("evidenceCount").GetInt32());
    }

    [Fact]
    public async Task EvidenceFromAnotherUserIsExcludedAcrossAllFamilies()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        var other = await RegisterAsync(otherClient);
        Authorize(ownerClient, owner);
        Authorize(otherClient, other);
        await SeedResumeAnalysisAsync(factory, owner.UserId, PracticeValues.Completed, ValidResumeOutput(70, [], []), DateTimeOffset.UtcNow);
        await SeedResumeAnalysisAsync(factory, other.UserId, PracticeValues.Completed, ValidResumeOutput(99, [], []), DateTimeOffset.UtcNow.AddMinutes(1));
        await SeedInterviewAsync(factory, owner.UserId, CanonicalScores(71), includeReport: true);
        await SeedInterviewAsync(factory, other.UserId, CanonicalScores(98), includeReport: true);
        await SeedStarAttemptAsync(factory, owner.UserId, StarEvaluationWithComponents(72));
        await SeedStarAttemptAsync(factory, other.UserId, StarEvaluationWithComponents(97));
        await SeedScenarioAttemptAsync(factory, owner.UserId, "Owner Competency", 73, PracticeFeatureValues.Completed);
        await SeedScenarioAttemptAsync(factory, other.UserId, "Other Competency", 96, PracticeFeatureValues.Completed);

        using var response = await ownerClient.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);
        var scores = data.GetProperty("competencies").EnumerateArray().ToArray();

        Assert.All(scores, item => Assert.DoesNotContain("other", item.GetProperty("code").GetString()!, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(scores, item => item.GetProperty("score").GetInt32() is 96 or 97 or 98 or 99);
        Assert.Contains(scores, item => item.GetProperty("score").GetInt32() is 70 or 71 or 72 or 73);
    }

    [Fact]
    public async Task MalformedHistoricalJsonIsIgnoredWhileValidEvidenceRemains()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed, "{not-json", DateTimeOffset.UtcNow);
        await SeedInterviewAsync(factory, account.UserId, CanonicalScores(75), includeReport: true);
        await SeedStarAttemptAsync(factory, account.UserId, "{not-json");
        await SeedScenarioAttemptAsync(factory, account.UserId, "Valid Scenario", 85, PracticeFeatureValues.Completed, "{not-json");

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);

        Assert.Contains(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "interview.correctness");
        Assert.DoesNotContain(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "scenario.valid_scenario");
    }

    [Fact]
    public async Task RepeatedReadsWithUnchangedDataHaveDeterministicOutput()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed, ValidResumeOutput(70, ["Gap"], []), DateTimeOffset.UtcNow);
        await SeedScenarioAttemptAsync(factory, account.UserId, "Z Competency", 80, PracticeFeatureValues.Completed);
        await SeedScenarioAttemptAsync(factory, account.UserId, "A Competency", 81, PracticeFeatureValues.Completed);

        using var firstResponse = await client.GetAsync("/api/v1/skill-profile");
        using var secondResponse = await client.GetAsync("/api/v1/skill-profile");
        var first = await DataAsync(firstResponse);
        var second = await DataAsync(secondResponse);

        Assert.Equal(first.GetRawText(), second.GetRawText());
        Assert.Equal(first.GetProperty("competencies").EnumerateArray().Select(item => item.GetProperty("code").GetString()),
            second.GetProperty("competencies").EnumerateArray().Select(item => item.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task ValidFieldBenchmarkBreakdownUsesItsCanonicalDimensions()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        var output = new ResumeAnalysisOutput(
            ["strength"], ["gap"], ["recommendation"],
            ReadinessScore: 80,
            Summary: "Field benchmark summary",
            SectionFeedback: ["section"],
            Breakdown: new Dictionary<string, int>
            {
                ["technicalFoundation"] = 61,
                ["projectEvidence"] = 62,
                ["experiencePresentation"] = 63,
                ["impactAchievements"] = 64,
                ["clarity"] = 65,
                ["roleAlignment"] = 66
            },
            Mode: ResumeAnalysisModes.FieldBenchmark);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed, output, DateTimeOffset.UtcNow);

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);

        Assert.Contains(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "resume.technical_foundation");
        Assert.Contains(data.GetProperty("competencies").EnumerateArray(),
            item => item.GetProperty("code").GetString() == "resume.role_alignment");
    }

    [Fact]
    public async Task InvalidStructuredScoresDoNotBecomeZeroCompetencies()
    {
        using var factory = NewFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed,
            ValidResumeOutput(101, ["qualitative gap"], []), DateTimeOffset.UtcNow);
        await SeedInterviewAsync(factory, account.UserId, CanonicalScores(80), includeReport: true,
            answerEvaluation: "{not-json", reportRubric: InvalidRubricJson());
        await SeedScenarioAttemptAsync(factory, account.UserId, "Invalid Scenario", 101, PracticeFeatureValues.Completed);

        using var response = await client.GetAsync("/api/v1/skill-profile");
        var data = await DataAsync(response);

        Assert.Empty(data.GetProperty("competencies").EnumerateArray());
        Assert.Empty(data.GetProperty("weaknessSignals").EnumerateArray());
    }

    private static NexoraApiFactory NewFactory() => new();

    private static void Authorize(HttpClient client, Account account) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

    private static async Task SeedResumeAnalysisAsync(
        NexoraApiFactory factory,
        Guid userId,
        string status,
        object result,
        DateTimeOffset completedAt)
    {
        var json = result is string raw ? raw : JsonSerializer.Serialize(result, JsonOptions);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = completedAt;
        var mode = result is ResumeAnalysisOutput output && !string.IsNullOrWhiteSpace(output.Mode)
            ? output.Mode
            : ResumeAnalysisModes.JobTargeted;
        var storedFile = new StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = $"skill-profile/{Guid.NewGuid():N}.pdf",
            FileName = "cv.pdf",
            ContentType = "application/pdf",
            Size = 100,
            Checksum = "test",
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
        JobDescription? jobDescription = null;
        if (mode == ResumeAnalysisModes.JobTargeted)
        {
            jobDescription = new JobDescription
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Title = "Skill profile target role",
                Content = "A deterministic job description for the skill profile test.",
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now
            };
        }
        var analysis = new ResumeAnalysis
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ResumeId = resume.Id,
            Mode = mode,
            JobDescriptionId = jobDescription?.Id,
            JobDescriptionVersion = jobDescription?.Version,
            Status = status,
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            Result = json,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = status == PracticeValues.Completed ? completedAt : null
        };
        db.AddRange(storedFile, resume, analysis);
        if (jobDescription is not null) db.JobDescriptions.Add(jobDescription);
        await db.SaveChangesAsync();
    }

    private static async Task SeedInterviewAsync(
        NexoraApiFactory factory,
        Guid userId,
        IReadOnlyCollection<RubricScore> scores,
        bool includeReport,
        DateTimeOffset? at = null,
        object? answerEvaluation = null,
        string? reportRubric = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = at ?? DateTimeOffset.UtcNow;
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
            PlanCodeSnapshot = "test",
            Status = BillingValues.Active,
            InterviewLimit = null,
            StartsAt = subscription.StartsAt,
            EndsAt = subscription.EndsAt,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        var reservation = new UsageEvent
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            EntitlementId = entitlement.Id,
            Action = BillingValues.Consume,
            Quantity = 1,
            SourceType = "interview",
            SourceId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            CreatedAt = now
        };
        var session = new InterviewSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ReservationEventId = reservation.Id,
            Role = "Backend Developer",
            Seniority = "senior",
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
            InterviewSessionId = session.Id,
            Sequence = 1,
            Kind = InterviewQuestionValues.Primary,
            Topic = InterviewQuestionValues.Technical,
            Content = "How do you design an API?",
            PromptVersion = "test",
            ModelVersion = "test",
            CreatedAt = now
        };
        var answer = new InterviewAnswer
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            InterviewSessionId = session.Id,
            QuestionId = question.Id,
            Content = "I design an API with clear contracts.",
            Evaluation = answerEvaluation is string rawEvaluation
                ? rawEvaluation
                : JsonSerializer.Serialize(answerEvaluation ?? AnswerEvaluationWithRubric(scores.First().Score), JsonOptions),
            CreatedAt = now
        };
        db.AddRange(subscription, entitlement, reservation, session, question, answer);
        if (includeReport)
        {
            db.InterviewReports.Add(new InterviewReport
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                InterviewSessionId = session.Id,
                OverallScore = (int)scores.Average(item => item.Score),
                Rubric = reportRubric ?? JsonSerializer.Serialize(scores, JsonOptions),
                Strengths = "[\"strength\"]",
                Gaps = "[\"gap\"]",
                ActionPlan = "[\"action\"]",
                Disclaimer = "test",
                ModelVersion = "test",
                PromptVersion = "test",
                RubricVersion = "test",
                SchemaVersion = "test",
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task SeedStarAttemptAsync(NexoraApiFactory factory, Guid userId, object evaluation)
    {
        var json = evaluation is string raw ? raw : JsonSerializer.Serialize(evaluation, JsonOptions);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.StarAttempts.Add(new StarAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Question = "Tell me about an incident.",
            Answer = "I resolved an incident.",
            Status = PracticeFeatureValues.Completed,
            EvaluationJson = json,
            ModelVersion = "test",
            PromptVersion = "test",
            SchemaVersion = "test",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedScenarioAttemptAsync(
        NexoraApiFactory factory,
        Guid userId,
        string competency,
        int score,
        string status,
        string? evaluationJson = null)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var category = await db.ScenarioCategories.SingleAsync(item => item.Slug == "banking");
        var now = DateTimeOffset.UtcNow;
        var scenario = new Scenario
        {
            Id = Guid.NewGuid(),
            Slug = $"skill-profile-{Guid.NewGuid():N}",
            Title = "Skill profile scenario",
            Summary = "A scenario for the skill profile integration test.",
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
        };
        var attempt = new ScenarioAttempt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ScenarioId = scenario.Id,
            Status = status,
            Answer = "Scenario answer",
            EvaluationJson = evaluationJson ?? JsonSerializer.Serialize(ScenarioEvaluation(score), JsonOptions),
            ModelVersion = "test",
            PromptVersion = "test",
            SchemaVersion = "test",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = status == PracticeFeatureValues.Completed ? now : null
        };
        db.AddRange(scenario, attempt);
        await db.SaveChangesAsync();
    }

    private static ResumeAnalysisOutput ValidResumeOutput(int clarity, IReadOnlyCollection<string> gaps, IReadOnlyCollection<string> missing) =>
        new(["strength"], gaps, ["recommendation"], MatchScore: clarity, Summary: "A grounded CV summary.",
            MatchedKeywordsOrSkills: ["C#"], MissingKeywordsOrSkills: missing, SectionFeedback: ["section"],
            Breakdown: new Dictionary<string, int>
            {
                ["technicalSkillMatch"] = clarity,
                ["experienceRelevance"] = clarity,
                ["impactEvidence"] = clarity,
                ["clarity"] = clarity,
                ["structure"] = clarity
            }, Mode: ResumeAnalysisModes.JobTargeted);

    private static RubricScore[] CanonicalScores(int score) =>
        CanonicalRubricValidator.RequiredCriteria.Select(criterion => new RubricScore(criterion, score, "Grounded answer evidence.")).ToArray();

    private static AnswerEvaluation AnswerEvaluationWithRubric(int score) =>
        new(CanonicalScores(score), "Grounded interview feedback.",
            new StarEvaluation(false, null, null, null, null, null, [], [], [], AiOperations.ScoreScale), AiOperations.ScoreScale);

    private static AnswerEvaluation AnswerEvaluationWithStar(int score, bool resultDetected = true) =>
        new(CanonicalScores(80), "Grounded interview feedback.", StarEvaluationWithComponents(score, resultDetected), AiOperations.ScoreScale);

    private static StarEvaluation StarEvaluationWithComponents(int score, bool resultDetected = true) =>
        new(true, score,
            new StarComponentEvaluation(score, true, "Situation evidence.", "Good situation."),
            new StarComponentEvaluation(score, true, "Task evidence.", "Good task."),
            new StarComponentEvaluation(score, true, "Action evidence.", "Good action."),
            resultDetected
                ? new StarComponentEvaluation(score, true, "Result evidence.", "Good result.")
                : new StarComponentEvaluation(0, false, string.Empty, "Missing result."),
            [], ["strength"], ["tip"], AiOperations.ScoreScale);

    private static ScenarioEvaluationResult ScenarioEvaluation(int score) => new(
        score,
        [
            new ScenarioDimensionEvaluation("analysis", score, "Analysis evidence.", "Keep analysis structured."),
            new ScenarioDimensionEvaluation("communication", score, "Communication evidence.", "Keep communication concise.")
        ],
        ["strength"], ["gap"], ["approach"], "Scenario feedback.", AiOperations.ScoreScale);

    private static string InvalidRubricJson() =>
        JsonSerializer.Serialize(CanonicalScores(101), JsonOptions);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"skill-profile-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Skill profile candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
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

    private static async Task<string[]> WeaknessLabelsAsync(HttpClient client)
    {
        using var response = await client.GetAsync("/api/v1/skill-profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        return data.GetProperty("weaknessSignals").EnumerateArray()
            .Select(item => item.GetProperty("label").GetString()!)
            .ToArray();
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
