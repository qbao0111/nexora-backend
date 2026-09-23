using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Business.Learning;
using Nexora.Business.Practice;
using Nexora.Business.Progress;
using Nexora.Business.Recommendations;
using Nexora.Business.Skills;
using Nexora.Data.Billing;
using Nexora.Data.Career;
using Nexora.Data.Learning;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;
using Xunit.Abstractions;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class ProgressDashboardApiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SqliteBootstrapReadsKeepDatabaseCommandsBounded()
    {
        var commands = new ReadCommandCounter();
        using var factory = new NexoraApiFactory(commands);
        await MeasureBootstrapReadsAsync(factory, commands);
    }

    [PostgresFact]
    public async Task PostgresBootstrapReadsKeepDatabaseCommandsBounded()
    {
        var commands = new ReadCommandCounter();
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        using var factory = NexoraApiFactory.CreatePostgres(connectionString, dbInterceptor: commands);
        await MeasureBootstrapReadsAsync(factory, commands);
    }

    private async Task MeasureBootstrapReadsAsync(NexoraApiFactory factory, ReadCommandCounter commands)
    {
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedFeatureEntitlementAsync(factory, account.UserId);
        await SeedResumeAnalysisAsync(factory, account.UserId, PracticeValues.Completed, DateTimeOffset.UtcNow);
        await SeedInterviewWithReportAsync(factory, account.UserId, 65, DateTimeOffset.UtcNow);
        await SeedLearningPathActivityAsync(factory, account.UserId, LearningPathValues.Pending, DateTimeOffset.UtcNow);

        foreach (var (route, maxCommands) in new[]
        {
            ("/api/v1/progress/dashboard", 23),
            ("/api/v1/me/career-profile", 9),
            ("/api/v1/recommendations/next", 8)
        })
        {
            using (var warm = await client.GetAsync(route)) Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
            var counts = new List<int>();
            var elapsed = new List<double>();
            var dbElapsed = new List<double>();
            for (var attempt = 0; attempt < 5; attempt++)
            {
                commands.Reset();
                var watch = Stopwatch.StartNew();
                using var response = await client.GetAsync(route);
                watch.Stop();
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                counts.Add(commands.Count);
                elapsed.Add(watch.Elapsed.TotalMilliseconds);
                dbElapsed.Add(commands.Duration.TotalMilliseconds);
            }

            output.WriteLine($"{route}: commands={string.Join(',', counts)}; warmMs={string.Join(',', elapsed.Select(value => value.ToString("F1", CultureInfo.InvariantCulture)))}; dbMs={string.Join(',', dbElapsed.Select(value => value.ToString("F1", CultureInfo.InvariantCulture)))}");
            Assert.All(counts, count => Assert.InRange(count, 1, maxCommands));
        }
    }

    private sealed class ReadCommandCounter : DbCommandInterceptor
    {
        private int _count;
        private long _durationTicks;

        public int Count => Volatile.Read(ref _count);
        public TimeSpan Duration => TimeSpan.FromTicks(Interlocked.Read(ref _durationTicks));
        public void Reset()
        {
            Interlocked.Exchange(ref _count, 0);
            Interlocked.Exchange(ref _durationTicks, 0);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Add(ref _durationTicks, eventData.Duration.Ticks);
            return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task DashboardRequiresAuthentication()
    {
        using var factory = NewFakeFactory(Profile(), Historical(), new FixedRecommendationService(null));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/api/v1/progress/dashboard");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DashboardPreservesProgressAnalyticsForbiddenBehavior()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);

        using var legacy = await client.GetAsync("/api/v1/progress");
        using var dashboard = await client.GetAsync("/api/v1/progress/dashboard");
        var legacyError = await ErrorAsync(legacy);
        var dashboardError = await ErrorAsync(dashboard);

        Assert.Equal(HttpStatusCode.Forbidden, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, dashboard.StatusCode);
        Assert.Equal("FEATURE_NOT_AVAILABLE", legacyError.GetProperty("code").GetString());
        Assert.Equal("FEATURE_NOT_AVAILABLE", dashboardError.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EntitledEmptyUserGetsAValidEmptyDashboard()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedFeatureEntitlementAsync(factory, account.UserId);

        using var response = await client.GetAsync("/api/v1/progress/dashboard");
        var data = await DataAsync(response);
        var readiness = data.GetProperty("readiness");
        using var legacy = await client.GetAsync("/api/v1/progress");
        var legacyData = await DataAsync(legacy);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(JsonValueKind.Null, readiness.GetProperty("score").ValueKind);
        Assert.Equal(0, readiness.GetProperty("assessedCompetencies").GetInt32());
        Assert.Equal(0, readiness.GetProperty("evidenceCount").GetInt32());
        Assert.Equal(0, readiness.GetProperty("priorityGapCount").GetInt32());
        Assert.Empty(data.GetProperty("weakestCompetencies").EnumerateArray());
        Assert.Empty(data.GetProperty("recentImprovements").EnumerateArray());
        Assert.Equal(0, data.GetProperty("weeklyCompletedActivities").GetProperty("total").GetInt32());
        Assert.Null(data.GetProperty("nextRecommendedPractice").GetString());
        Assert.Equal(0, data.GetProperty("historicalStats").GetProperty("completedInterviews").GetInt32());
        Assert.Equal(0, legacyData.GetProperty("completedInterviews").GetInt32());
    }

    [Fact]
    public async Task DashboardReturnsRecentScoresAndActivityInDescendingOrderWithoutCrossUserRows()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        Authorize(ownerClient, owner);
        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient);
        Authorize(otherClient, other);
        await SeedFeatureEntitlementAsync(factory, owner.UserId);

        await SeedInterviewWithReportAsync(factory, owner.UserId, 61, At(2));
        await SeedInterviewWithReportAsync(factory, owner.UserId, 89, At(3));
        await SeedScenarioAttemptAsync(factory, owner.UserId, PracticeFeatureValues.Completed, At(4));
        await SeedStarAttemptAsync(factory, owner.UserId, PracticeFeatureValues.Completed, At(5));
        await SeedScenarioAttemptAsync(factory, other.UserId, PracticeFeatureValues.Completed, At(6));

        using var response = await ownerClient.GetAsync("/api/v1/progress/dashboard");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        var historical = data.GetProperty("historicalStats");
        var scores = historical.GetProperty("recentInterviewScores").EnumerateArray().ToArray();
        var activity = historical.GetProperty("recentActivity").EnumerateArray().ToArray();

        Assert.Equal(2, historical.GetProperty("completedInterviews").GetInt32());
        Assert.Equal(89, scores[0].GetProperty("score").GetInt32());
        Assert.Equal(61, scores[1].GetProperty("score").GetInt32());
        Assert.Equal(4, activity.Length);
        Assert.Equal("star", activity[0].GetProperty("kind").GetString());
        Assert.Equal("scenario", activity[1].GetProperty("kind").GetString());
        Assert.Equal("interview", activity[2].GetProperty("kind").GetString());
        Assert.Equal("interview", activity[3].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task DashboardMapsReadinessImprovementsRecommendationAndLegacyStats()
    {
        var historical = new ProgressView(
            2,
            [
                new RecentInterviewScore(Id(3), 75, At(3)),
                new RecentInterviewScore(Id(2), 60, At(2)),
                new RecentInterviewScore(Id(1), 60, At(1))
            ],
            70,
            new ProgressStarAverages(70, 71, 72, 73),
            4,
            74,
            3,
            [new RecentActivity("interview", Id(3), At(3))]);
        var profile = new SkillProfileView(
            [
                Competency("interview.clarity", "Clarity", "interview", 52, 4, At(2)),
                Competency("resume.structure", "Structure", "resume", 70, 3, At(3)),
                Competency("scenario.problem_solving", "Problem Solving", "scenario", 80, 2, At(4))
            ],
            [new SkillProfileWeaknessSignal("cv_analysis", "Missing SQL", At(5))]);
        var recommendation = new NextPracticeRecommendationView(
            "Practice Clarity next.",
            LearningPathValues.Interview,
            Id(9),
            20,
            1,
            new NextPracticeActionView("practice_again", InterviewPracticeValues.Recommendation, Id(9), null, "clarity", null),
            new NextPracticeRecommendationRationaleView("interview.clarity", "Clarity", 4, false));

        using var factory = NewFakeFactory(profile, historical, new FixedRecommendationService(recommendation));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);

        using var response = await client.GetAsync("/api/v1/progress/dashboard");
        var data = await DataAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(67, data.GetProperty("readiness").GetProperty("score").GetInt32());
        Assert.Equal(3, data.GetProperty("readiness").GetProperty("assessedCompetencies").GetInt32());
        Assert.Equal(9, data.GetProperty("readiness").GetProperty("evidenceCount").GetInt32());
        Assert.Equal(2, data.GetProperty("readiness").GetProperty("priorityGapCount").GetInt32());
        Assert.Equal(1, data.GetProperty("readiness").GetProperty("qualitativeWeaknessCount").GetInt32());
        Assert.Equal("interview.clarity", data.GetProperty("weakestCompetencies")[0].GetProperty("code").GetString());
        var improvement = Assert.Single(data.GetProperty("recentImprovements").EnumerateArray());
        Assert.Equal(15, improvement.GetProperty("delta").GetInt32());
        Assert.Equal("interview", improvement.GetProperty("kind").GetString());
        var nextPractice = data.GetProperty("nextRecommendedPractice");
        Assert.Equal(20, nextPractice.GetProperty("estimatedMinutes").GetInt32());
        Assert.Equal("practice_again", nextPractice.GetProperty("action").GetProperty("type").GetString());
        Assert.Equal("interview.clarity", nextPractice.GetProperty("rationale").GetProperty("competencyCode").GetString());
        Assert.Equal("Clarity", nextPractice.GetProperty("rationale").GetProperty("competencyName").GetString());
        Assert.Equal(2, data.GetProperty("historicalStats").GetProperty("completedInterviews").GetInt32());
    }

    [Fact]
    public async Task WeeklyCountsUseUtcWeekOwnerBoundaryAndExcludeIncompleteRows()
    {
        using var factory = NewFakeFactory(Profile(), Historical(), new FixedRecommendationService(null));
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        Authorize(ownerClient, owner);
        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient);
        Authorize(otherClient, other);

        var capturedNow = DateTimeOffset.UtcNow;
        var expectedWeekStart = ProgressDashboardPolicy.GetUtcWeekStart(capturedNow);
        await SeedResumeAnalysisAsync(factory, owner.UserId, PracticeValues.Completed, capturedNow);
        await SeedInterviewAsync(factory, owner.UserId, capturedNow);
        await SeedScenarioAttemptAsync(factory, owner.UserId, PracticeFeatureValues.Completed, capturedNow);
        await SeedStarAttemptAsync(factory, owner.UserId, PracticeFeatureValues.Completed, capturedNow);
        await SeedLearningPathActivityAsync(factory, owner.UserId, LearningPathValues.Completed, capturedNow);
        await SeedScenarioAttemptAsync(factory, owner.UserId, PracticeFeatureValues.Completed, expectedWeekStart.AddDays(-1));
        await SeedScenarioAttemptAsync(factory, owner.UserId, PracticeFeatureValues.Failed, capturedNow);
        await SeedScenarioAttemptAsync(factory, owner.UserId, PracticeFeatureValues.Completed, null);
        await SeedScenarioAttemptAsync(factory, other.UserId, PracticeFeatureValues.Completed, capturedNow);

        using var response = await ownerClient.GetAsync("/api/v1/progress/dashboard");
        var weekly = (await DataAsync(response)).GetProperty("weeklyCompletedActivities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedWeekStart, weekly.GetProperty("windowStart").GetDateTimeOffset());
        Assert.Equal(5, weekly.GetProperty("total").GetInt32());
        Assert.Equal(1, weekly.GetProperty("resumeAnalyses").GetInt32());
        Assert.Equal(1, weekly.GetProperty("interviews").GetInt32());
        Assert.Equal(1, weekly.GetProperty("scenarios").GetInt32());
        Assert.Equal(1, weekly.GetProperty("starAttempts").GetInt32());
        Assert.Equal(1, weekly.GetProperty("learningPathActivities").GetInt32());
    }

    [Fact]
    public async Task MissingCareerGoalDoesNotBreakTheDashboard()
    {
        using var factory = NewFakeFactory(Profile(), Historical());
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);

        using var response = await client.GetAsync("/api/v1/progress/dashboard");
        var data = await DataAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(data.GetProperty("nextRecommendedPractice").GetString());
    }

    [Fact]
    public async Task MissingLearningPathDoesNotBreakTheDashboard()
    {
        using var factory = NewFakeFactory(Profile(), Historical());
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await CreateCareerGoalAsync(client);

        using var response = await client.GetAsync("/api/v1/progress/dashboard");
        var data = await DataAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(data.GetProperty("nextRecommendedPractice").GetString());
    }

    [Fact]
    public async Task RepeatedDashboardReadsDoNotMutateLearningPathRows()
    {
        var now = DateTimeOffset.UtcNow;
        using var factory = NewFakeFactory(Profile(), Historical(), new FixedRecommendationService(null), new FixedTimeProvider(now));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        Authorize(client, account);
        await SeedLearningPathActivityAsync(factory, account.UserId, LearningPathValues.Completed, now);

        using var first = await client.GetAsync("/api/v1/progress/dashboard");
        using var second = await client.GetAsync("/api/v1/progress/dashboard");
        var firstBody = await first.Content.ReadAsStringAsync();
        var secondBody = await second.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(firstBody, secondBody);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.LearningPathActivities.CountAsync(item => item.LearningPath.UserId == account.UserId));
    }

    private static NexoraApiFactory NewFakeFactory(
        SkillProfileView profile,
        ProgressView historical,
        INextPracticeRecommendationService? recommendation = null,
        TimeProvider? timeProvider = null) =>
        new(new Dictionary<string, string?>(), services =>
        {
            services.RemoveAll<IProgressService>();
            services.AddSingleton<IProgressService>(new FixedProgressService(historical));
            services.RemoveAll<ISkillProfileService>();
            services.AddSingleton<ISkillProfileService>(new FixedSkillProfileService(profile));
            if (recommendation is not null)
            {
                services.RemoveAll<INextPracticeRecommendationService>();
                services.AddSingleton<INextPracticeRecommendationService>(recommendation);
            }
            if (timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(timeProvider);
            }
        });

    private static SkillProfileView Profile(params SkillProfileCompetency[] competencies) => new(competencies, []);

    private static SkillProfileCompetency Competency(
        string code,
        string name,
        string category,
        int score,
        int evidenceCount,
        DateTimeOffset latestEvidenceAt) =>
        new(code, name, category, score, evidenceCount, latestEvidenceAt, []);

    private static ProgressView Historical() => new(0, [], null, null, 0, null, 0, []);

    private static DateTimeOffset At(int day) => new(2026, 9, day, 0, 0, 0, TimeSpan.Zero);

    private static Guid Id(int value) => Guid.Parse($"81000000-0000-0000-0000-{value:000000000001}");

    private static void Authorize(HttpClient client, Account account) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"progress-dashboard-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Progress dashboard candidate"
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task CreateCareerGoalAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole = "Backend Developer",
            seniority = "senior"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task SeedFeatureEntitlementAsync(NexoraApiFactory factory, Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"db-{Guid.NewGuid():N}", Name = "Dashboard test", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 1, Currency = "VND", DurationDays = 30, InterviewQuota = 1, IsActive = true, CreatedAt = now };
        var subscription = new Subscription { Id = Guid.NewGuid(), UserId = userId, Status = BillingValues.Active, StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(), UserId = userId, SubscriptionId = subscription.Id, PlanCodeSnapshot = plan.Code,
            Status = BillingValues.Active, InterviewLimit = 1, StartsAt = subscription.StartsAt, EndsAt = subscription.EndsAt,
            CreatedAt = now, UpdatedAt = now, ConcurrencyToken = Guid.NewGuid()
        };
        var feature = await db.FeatureDefinitions.SingleAsync(item => item.Code == FeatureValues.ProgressAnalytics);
        var entitlementFeature = new EntitlementFeature
        {
            Id = Guid.NewGuid(), EntitlementId = entitlement.Id, FeatureDefinitionId = feature.Id,
            FeatureCode = FeatureValues.ProgressAnalytics, IsEnabled = true, Limit = null,
            CreatedAt = now, UpdatedAt = now, ConcurrencyToken = Guid.NewGuid()
        };
        db.AddRange(plan, price, subscription, entitlement, entitlementFeature);
        await db.SaveChangesAsync();
    }

    private static async Task SeedResumeAnalysisAsync(
        NexoraApiFactory factory,
        Guid userId,
        string status,
        DateTimeOffset? completedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = completedAt ?? DateTimeOffset.UtcNow;
        var file = new StoredFile
        {
            Id = Guid.NewGuid(), UserId = userId, StorageKey = $"dashboard/{Guid.NewGuid():N}", FileName = "cv.pdf",
            ContentType = "application/pdf", Size = 10, Checksum = Guid.NewGuid().ToString("N"), CreatedAt = now
        };
        var resume = new ResumeRecord
        {
            Id = Guid.NewGuid(), UserId = userId, StoredFileId = file.Id, Status = PracticeValues.Ready,
            Version = 1, CreatedAt = now, UpdatedAt = now
        };
        db.ResumeAnalyses.Add(new ResumeAnalysis
        {
            Id = Guid.NewGuid(), UserId = userId, ResumeId = resume.Id, Mode = "field_benchmark", Status = status,
            ModelVersion = "test", PromptVersion = "test", SchemaVersion = "test", Result = "{}",
            CreatedAt = now, UpdatedAt = now, CompletedAt = completedAt
        });
        db.AddRange(file, resume);
        await db.SaveChangesAsync();
    }

    private static async Task SeedInterviewAsync(NexoraApiFactory factory, Guid userId, DateTimeOffset completedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), UserId = userId, Status = BillingValues.Active, StartsAt = completedAt.AddDays(-1),
            EndsAt = completedAt.AddDays(30), CreatedAt = completedAt, UpdatedAt = completedAt
        };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(), UserId = userId, SubscriptionId = subscription.Id, PlanCodeSnapshot = "dashboard-test",
            Status = BillingValues.Active, StartsAt = subscription.StartsAt, EndsAt = subscription.EndsAt,
            CreatedAt = completedAt, UpdatedAt = completedAt, ConcurrencyToken = Guid.NewGuid()
        };
        var reservation = new UsageEvent
        {
            Id = Guid.NewGuid(), UserId = userId, EntitlementId = entitlement.Id, Action = BillingValues.Consume,
            Quantity = 1, SourceType = "interview", SourceId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString("N"), CreatedAt = completedAt
        };
        db.InterviewSessions.Add(new InterviewSession
        {
            Id = Guid.NewGuid(), UserId = userId, ReservationEventId = reservation.Id, Role = "Backend Developer",
            Seniority = "senior", InterviewType = "technical", Difficulty = "medium", Status = PracticeValues.Completed,
            Version = 1, CreatedAt = completedAt, UpdatedAt = completedAt, CompletedAt = completedAt
        });
        db.AddRange(subscription, entitlement, reservation);
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SeedInterviewWithReportAsync(
        NexoraApiFactory factory,
        Guid userId,
        int score,
        DateTimeOffset completedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(), UserId = userId, Status = BillingValues.Active, StartsAt = completedAt.AddDays(-1),
            EndsAt = completedAt.AddYears(100), CreatedAt = completedAt, UpdatedAt = completedAt
        };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(), UserId = userId, SubscriptionId = subscription.Id, PlanCodeSnapshot = "dashboard-report-test",
            Status = BillingValues.Active, StartsAt = subscription.StartsAt, EndsAt = subscription.EndsAt,
            CreatedAt = completedAt, UpdatedAt = completedAt, ConcurrencyToken = Guid.NewGuid()
        };
        var reservation = new UsageEvent
        {
            Id = Guid.NewGuid(), UserId = userId, EntitlementId = entitlement.Id, Action = BillingValues.Consume,
            Quantity = 1, SourceType = "interview", SourceId = Guid.NewGuid().ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString("N"), CreatedAt = completedAt
        };
        var session = new InterviewSession
        {
            Id = Guid.NewGuid(), UserId = userId, ReservationEventId = reservation.Id, Role = "Backend Developer",
            Seniority = "senior", InterviewType = "technical", Difficulty = "medium", Status = PracticeValues.Completed,
            Version = 1, CreatedAt = completedAt, UpdatedAt = completedAt, CompletedAt = completedAt
        };
        var report = new InterviewReport
        {
            Id = Guid.NewGuid(), UserId = userId, InterviewSessionId = session.Id, OverallScore = score,
            Rubric = "[]", Strengths = "[]", Gaps = "[]", ActionPlan = "[]", Disclaimer = "test",
            ModelVersion = "test", PromptVersion = "test", RubricVersion = "test", SchemaVersion = "test", CreatedAt = completedAt
        };
        db.AddRange(subscription, entitlement, reservation, session, report);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private static async Task SeedScenarioAttemptAsync(
        NexoraApiFactory factory,
        Guid userId,
        string status,
        DateTimeOffset? completedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = completedAt ?? DateTimeOffset.UtcNow;
        var category = await db.ScenarioCategories.SingleAsync(item => item.Slug == "banking");
        var scenario = new Scenario
        {
            Id = Guid.NewGuid(), Slug = $"dashboard-{Guid.NewGuid():N}", Title = "Dashboard scenario", Summary = "Dashboard test scenario",
            CategoryId = category.Id, Difficulty = "medium", Competency = "problem_solving", EstimatedMinutes = 15,
            Content = "Test content", SortOrder = 1, Status = PracticeFeatureValues.Published, CreatedAt = now, UpdatedAt = now, PublishedAt = now
        };
        db.ScenarioAttempts.Add(new ScenarioAttempt
        {
            Id = Guid.NewGuid(), UserId = userId, ScenarioId = scenario.Id, Status = status, Answer = status == PracticeFeatureValues.Completed ? "answer" : null,
            CreatedAt = now, UpdatedAt = now, CompletedAt = completedAt
        });
        db.Scenarios.Add(scenario);
        await db.SaveChangesAsync();
    }

    private static async Task SeedStarAttemptAsync(NexoraApiFactory factory, Guid userId, string status, DateTimeOffset completedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        db.StarAttempts.Add(new StarAttempt
        {
            Id = Guid.NewGuid(), UserId = userId, Question = "Tell me about a result.", Answer = "I delivered a result.",
            Status = status, CreatedAt = completedAt, UpdatedAt = completedAt, CompletedAt = status == PracticeFeatureValues.Completed ? completedAt : null
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedLearningPathActivityAsync(NexoraApiFactory factory, Guid userId, string status, DateTimeOffset completedAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var goal = new CareerGoal
        {
            Id = Guid.NewGuid(), UserId = userId, TargetRole = "Backend Developer", Seniority = "senior", Active = true,
            CreatedAt = completedAt, UpdatedAt = completedAt
        };
        var path = new LearningPath
        {
            Id = Guid.NewGuid(), UserId = userId, CareerGoalId = goal.Id, Status = LearningPathValues.Active,
            CreatedAt = completedAt, UpdatedAt = completedAt
        };
        var milestone = new LearningPathMilestone
        {
            Id = Guid.NewGuid(), LearningPathId = path.Id, Code = LearningPathValues.CriticalMilestone,
            Title = "Critical gaps", SortOrder = 0, Status = LearningPathValues.Active, CreatedAt = completedAt, UpdatedAt = completedAt
        };
        db.LearningPathActivities.Add(new LearningPathActivity
        {
            Id = Guid.NewGuid(), LearningPathId = path.Id, LearningPathMilestoneId = milestone.Id,
            ActivityKey = $"db:{Guid.NewGuid():N}", Type = LearningPathValues.Interview, Title = "Dashboard activity",
            Description = "Dashboard test activity", Priority = 1, SortOrder = 0, Status = status,
            CreatedAt = completedAt, UpdatedAt = completedAt, CompletedAt = status == LearningPathValues.Completed ? completedAt : null
        });
        db.AddRange(goal, path, milestone);
        await db.SaveChangesAsync();
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"Expected a successful response but got {(int)response.StatusCode} {response.StatusCode}. Body: {body}");

        using var document = JsonDocument.Parse(body);
        Assert.True(
            document.RootElement.TryGetProperty("data", out var data),
            $"Expected response body to contain a data property. Body: {body}");
        return data.Clone();
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);

    private sealed class FixedProgressService(ProgressView view) : IProgressService
    {
        public Task<ProgressView> GetAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(view);
    }

    private sealed class FixedSkillProfileService(SkillProfileView view) : ISkillProfileService
    {
        public Task<SkillProfileView> GetAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(view);
    }

    private sealed class FixedRecommendationService(NextPracticeRecommendationView? view) : INextPracticeRecommendationService
    {
        public Task<NextPracticeRecommendationView?> GetAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(view);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

}
