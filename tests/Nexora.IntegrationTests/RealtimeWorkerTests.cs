using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;
using Nexora.Data.Realtime;
using UglyToad.PdfPig.Writer;
using static Nexora.IntegrationTests.RealtimeApiTests;

namespace Nexora.IntegrationTests;

public sealed class RealtimeWorkerTests
{
    private const string DocxType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeWorkerPublishesReadyOrFailedAndRestMatches(bool fail)
    {
        using var factory = CreateFactory(configure: services =>
        {
            services.RemoveAll<IDocumentOcrProvider>();
            services.AddSingleton<IDocumentOcrProvider, FailingOcr>();
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var socket = await ConnectAsync(factory, owner.Token);
        var id = await UploadResumeAsync(client, fail);
        using (var before = factory.Services.CreateScope())
            Assert.Empty(await before.ServiceProvider.GetRequiredService<NexoraDbContext>().RealtimeNotifications.ToArrayAsync());
        await ProcessJobsAsync(factory);
        var expected = fail ? "failed" : "ready";
        await AssertNotificationAsync(factory, owner.UserId, "resume", id, expected);
        using var broadcaster = CreateBroadcaster(factory);
        await broadcaster.BroadcastPendingAsync(CancellationToken.None);
        var message = await socket.ReadEventAsync();
        Assert.Equal(expected, message.GetProperty("status").GetString());
        using var response = await client.GetAsync($"/api/v1/resumes/{id}");
        Assert.Equal(expected, (await DataAsync(response)).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumeAnalysisEnqueuesOnlyFinalState(bool fail)
    {
        var ai = new TestAiProvider();
        if (fail) ai.EnqueueResponse(AiPurposes.ResumeAnalysis, new TimeoutException("Synthetic failure"));
        using var factory = CreateFactory(configure: services =>
        {
            services.RemoveAll<IAiProvider>();
            services.AddSingleton<IAiProvider>(ai);
            services.RemoveAll<IDocumentOcrProvider>();
            services.AddSingleton<IDocumentOcrProvider, FailingOcr>();
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var resumeId = await UploadResumeAsync(client, false);
        await ProcessJobsAsync(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var feature = await db.EntitlementFeatures.Include(item => item.Entitlement)
                .SingleAsync(item => item.Entitlement.UserId == owner.UserId && item.FeatureCode == FeatureValues.CvAnalysis);
            feature.IsEnabled = true;
            feature.Limit = 1;
            await db.SaveChangesAsync();
        }
        var jd = await PostAsync(client, "/api/v1/job-descriptions", new { title = "Backend", content = "C# and PostgreSQL development" });
        var analysis = await PostAsync(client, "/api/v1/resume-analyses", new { resumeId, jobDescriptionId = jd.GetProperty("id").GetGuid() });
        var id = analysis.GetProperty("id").GetGuid();
        Assert.Equal("queued", analysis.GetProperty("status").GetString());
        await ProcessJobsAsync(factory);
        var expected = fail ? "failed" : "completed";
        await AssertNotificationAsync(factory, owner.UserId, "resumeAnalysis", id, expected);
        using var response = await client.GetAsync($"/api/v1/resume-analyses/{id}");
        Assert.Equal(expected, (await DataAsync(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ActivationFailureEnqueuesFailedWithVoidedQuota()
    {
        var ai = new TestAiProvider();
        ai.EnqueueResponse(AiPurposes.InterviewFirstQuestion, new TimeoutException("Synthetic failure"));
        using var factory = new NexoraApiFactory(ai);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var session = await PostAsync(client, "/api/v1/interviews", InterviewInput());
        var id = session.GetProperty("id").GetGuid();
        await ProcessJobsAsync(factory);
        await AssertNotificationAsync(factory, owner.UserId, "interview", id, "failed");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal("failed", (await db.InterviewSessions.SingleAsync()).Status);
        Assert.Empty(await db.InterviewQuestions.ToArrayAsync());
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.Action == BillingValues.Void));
        Assert.Equal(0, await db.UsageEvents.CountAsync(item => item.Action == BillingValues.Consume));
    }

    [Fact]
    public async Task NotificationWriteFailureRollsBackActivationQuestionAndConsumptionTogether()
    {
        var interceptor = new RejectActiveNotificationOnce();
        using var factory = CreateFactory(configure: services =>
            services.ConfigureDbContext<NexoraDbContext>(options => options.AddInterceptors(interceptor)));
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        var session = await PostAsync(client, "/api/v1/interviews", InterviewInput());
        await ProcessJobsAsync(factory);
        Assert.True(interceptor.Rejected);
        await AssertNotificationAsync(factory, owner.UserId, "interview", session.GetProperty("id").GetGuid(), "failed");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal("failed", (await db.InterviewSessions.SingleAsync()).Status);
        Assert.Empty(await db.InterviewQuestions.ToArrayAsync());
        Assert.Equal(0, await db.UsageEvents.CountAsync(item => item.Action == BillingValues.Consume));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.Action == BillingValues.Void));
    }

    [Fact]
    public async Task ScenarioAndStarWorkersPublishCompletedEventsAndRestMatches()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        await SeedFeatureEntitlementsAsync(factory, owner.UserId, [FeatureValues.Scenario, FeatureValues.StarBuilder], 1);
        var scenario = await SeedPublishedScenarioAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var socket = await ConnectAsync(factory, owner.Token);

        var scenarioAttempt = await PostAsync(client, "/api/v1/scenario-attempts", new { scenarioId = scenario.Id });
        var scenarioAttemptId = scenarioAttempt.GetProperty("id").GetGuid();
        await PostAsync(client, $"/api/v1/scenario-attempts/{scenarioAttemptId}/submit", new { answer = "I would analyze the root cause, align stakeholders and measure the result." });
        var starAttempt = await PostAsync(client, "/api/v1/star-attempts", new
        {
            question = "Hãy kể về một lần bạn giải quyết vấn đề.",
            answer = "Tôi phân tích nguyên nhân, thực hiện thay đổi và đo kết quả."
        });
        var starAttemptId = starAttempt.GetProperty("id").GetGuid();

        await ProcessScenarioStarJobsAsync(factory);

        await AssertNotificationAsync(factory, owner.UserId, "scenarioAttempt", scenarioAttemptId, PracticeFeatureValues.Completed);
        await AssertNotificationAsync(factory, owner.UserId, "starAttempt", starAttemptId, PracticeFeatureValues.Completed);
        using var broadcaster = CreateBroadcaster(factory);
        Assert.Equal(2, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        var events = new[] { await socket.ReadEventAsync(), await socket.ReadEventAsync() };
        Assert.Contains(events, item => item.GetProperty("resourceType").GetString() == "scenarioAttempt" &&
            item.GetProperty("resourceId").GetGuid() == scenarioAttemptId && item.GetProperty("status").GetString() == PracticeFeatureValues.Completed);
        Assert.Contains(events, item => item.GetProperty("resourceType").GetString() == "starAttempt" &&
            item.GetProperty("resourceId").GetGuid() == starAttemptId && item.GetProperty("status").GetString() == PracticeFeatureValues.Completed);

        using var scenarioResponse = await client.GetAsync($"/api/v1/scenario-attempts/{scenarioAttemptId}");
        Assert.Equal(PracticeFeatureValues.Completed, (await DataAsync(scenarioResponse)).GetProperty("status").GetString());
        using var starResponse = await client.GetAsync($"/api/v1/star-attempts/{starAttemptId}");
        Assert.Equal(PracticeFeatureValues.Completed, (await DataAsync(starResponse)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task ScenarioAndStarWorkerFailuresPublishFailedEventsAndRestMatches()
    {
        var ai = new TestAiProvider();
        ai.EnqueueResponse(AiPurposes.ScenarioEvaluate, new TimeoutException("Synthetic scenario failure"));
        ai.EnqueueResponse(AiPurposes.ScenarioEvaluate, new TimeoutException("Synthetic scenario failure retry"));
        ai.EnqueueResponse(AiPurposes.StarEvaluate, new TimeoutException("Synthetic STAR failure"));
        ai.EnqueueResponse(AiPurposes.StarEvaluate, new TimeoutException("Synthetic STAR failure retry"));
        using var factory = CreateFactory(configure: services =>
        {
            services.RemoveAll<IAiProvider>();
            services.AddSingleton<IAiProvider>(ai);
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        await SeedFeatureEntitlementsAsync(factory, owner.UserId, [FeatureValues.Scenario, FeatureValues.StarBuilder], 1);
        var scenario = await SeedPublishedScenarioAsync(factory);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.Token);
        using var socket = await ConnectAsync(factory, owner.Token);

        var scenarioAttempt = await PostAsync(client, "/api/v1/scenario-attempts", new { scenarioId = scenario.Id });
        var scenarioAttemptId = scenarioAttempt.GetProperty("id").GetGuid();
        await PostAsync(client, $"/api/v1/scenario-attempts/{scenarioAttemptId}/submit", new { answer = "Synthetic answer." });
        var starAttempt = await PostAsync(client, "/api/v1/star-attempts", new
        {
            question = "Hãy kể về một lần bạn giải quyết vấn đề.",
            answer = "Synthetic STAR answer."
        });
        var starAttemptId = starAttempt.GetProperty("id").GetGuid();

        await ProcessScenarioStarJobsAsync(factory);

        await AssertNotificationAsync(factory, owner.UserId, "scenarioAttempt", scenarioAttemptId, PracticeFeatureValues.Failed);
        await AssertNotificationAsync(factory, owner.UserId, "starAttempt", starAttemptId, PracticeFeatureValues.Failed);
        using var broadcaster = CreateBroadcaster(factory);
        Assert.Equal(2, await broadcaster.BroadcastPendingAsync(CancellationToken.None));
        var events = new[] { await socket.ReadEventAsync(), await socket.ReadEventAsync() };
        Assert.All(events, item => Assert.Equal(PracticeFeatureValues.Failed, item.GetProperty("status").GetString()));

        using var scenarioResponse = await client.GetAsync($"/api/v1/scenario-attempts/{scenarioAttemptId}");
        Assert.Equal(PracticeFeatureValues.Failed, (await DataAsync(scenarioResponse)).GetProperty("status").GetString());
        using var starResponse = await client.GetAsync($"/api/v1/star-attempts/{starAttemptId}");
        Assert.Equal(PracticeFeatureValues.Failed, (await DataAsync(starResponse)).GetProperty("status").GetString());
    }

    private static async Task AssertNotificationAsync(NexoraApiFactory factory, Guid userId, string type, Guid id, string status)
    {
        using var scope = factory.Services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().RealtimeNotifications
            .Where(item => item.ResourceId == id).ToArrayAsync();
        var notification = Assert.Single(rows);
        Assert.Equal(userId, notification.UserId);
        Assert.Equal(type, notification.ResourceType);
        Assert.Equal(status, notification.Status);
        Assert.Null(notification.ProcessedAt);
    }

    private static async Task SeedFeatureEntitlementsAsync(NexoraApiFactory factory, Guid userId, IReadOnlyCollection<string> featureCodes, int limit)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"realtime-feature-{Guid.NewGuid():N}", Name = "Realtime feature test", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 1, Currency = "VND", DurationDays = 30, InterviewQuota = 1, IsActive = true, CreatedAt = now };
        var subscription = new Subscription { Id = Guid.NewGuid(), UserId = userId, Status = BillingValues.Active, StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now };
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
        db.AddRange(plan, price, subscription, entitlement);
        foreach (var featureCode in featureCodes)
        {
            var featureDefinition = await db.FeatureDefinitions.SingleAsync(item => item.Code == featureCode);
            db.EntitlementFeatures.Add(new EntitlementFeature
            {
                Id = Guid.NewGuid(),
                EntitlementId = entitlement.Id,
                FeatureDefinitionId = featureDefinition.Id,
                FeatureCode = featureCode,
                IsEnabled = true,
                Limit = limit,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid()
            });
        }
        await db.SaveChangesAsync();
    }

    private static async Task<Scenario> SeedPublishedScenarioAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var category = await db.ScenarioCategories.FirstAsync();
        var scenario = new Scenario
        {
            Id = Guid.NewGuid(),
            Slug = $"realtime-scenario-{Guid.NewGuid():N}",
            Title = "Realtime scenario test",
            Summary = "Published scenario for SignalR tests",
            CategoryId = category.Id,
            Difficulty = "medium",
            Competency = "problem_analysis",
            EstimatedMinutes = 15,
            Content = "A scenario body for deterministic realtime notification tests.",
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

    private static async Task ProcessScenarioStarJobsAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IScenarioStarJobProcessor>().ProcessPendingAsync(CancellationToken.None);
    }

    private static async Task<Guid> UploadResumeAsync(HttpClient client, bool invalid)
    {
        byte[] bytes;
        if (invalid)
        {
            var pdf = new PdfDocumentBuilder();
            pdf.AddPage(612, 792);
            bytes = pdf.Build();
        }
        else
        {
            using var stream = new MemoryStream();
            using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
            {
                var main = document.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text(
                    "Synthetic candidate with experience in C# backend development, PostgreSQL database design, automated integration tests and reliable asynchronous processing.")))));
                main.Document.Save();
            }
            bytes = stream.ToArray();
        }
        var intent = await PostAsync(client, "/api/v1/uploads/presign", new
        { fileName = invalid ? "cv.pdf" : "cv.docx", contentType = invalid ? "application/pdf" : DocxType, size = bytes.Length });
        using var body = new ByteArrayContent(bytes);
        using var uploaded = await client.PutAsync(intent.GetProperty("uploadUrl").GetString(), body);
        Assert.Equal(HttpStatusCode.NoContent, uploaded.StatusCode);
        var resume = await PostAsync(client, "/api/v1/resumes", new { uploadToken = intent.GetProperty("token").GetString() });
        return resume.GetProperty("id").GetGuid();
    }

    private sealed class FailingOcr : IDocumentOcrProvider
    {
        public Task<DocumentOcrResult> ExtractAsync(Stream content, string contentType, CancellationToken cancellationToken) =>
            Task.FromException<DocumentOcrResult>(new InvalidDataException("Synthetic document fallback failure"));
    }

    private sealed class RejectActiveNotificationOnce : SaveChangesInterceptor
    {
        public bool Rejected { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Rejected && eventData.Context!.ChangeTracker.Entries<RealtimeNotification>()
                .Any(item => item.State == EntityState.Added && item.Entity.Status == "active"))
            {
                Rejected = true;
                throw new DbUpdateException("Synthetic notification insert failure");
            }
            return ValueTask.FromResult(result);
        }
    }
}
