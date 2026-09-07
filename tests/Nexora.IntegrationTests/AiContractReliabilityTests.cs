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

namespace Nexora.IntegrationTests;

public sealed class AiContractReliabilityTests
{
    [Fact]
    public async Task StagingIncidentReproductionFixedNonBehavioralWithStarApplicableTrueNormalizesAndSucceeds()
    {
        var aiProvider = new TestAiProvider();
        // Script interview.evaluate for non-behavioral question where AI erroneously returns star.applicable = true
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [
                new RubricScore("correctness", 85, "Good technical explanation of concepts."),
                new RubricScore("structure", 80, "Organized clearly."),
                new RubricScore("completeness", 75, "Detailed technical answer."),
                new RubricScore("clarity", 90, "Crisp and concise.")
            ],
            "Good technical answer.",
            new StarEvaluation(
                Applicable: true, // Erroneous true from Gemini on technical question
                OverallScore: 80,
                Situation: new StarComponentEvaluation(80, true, "Sit evidence", "Sit fb"),
                Task: new StarComponentEvaluation(80, true, "Task evidence", "Task fb"),
                Action: new StarComponentEvaluation(80, true, "Act evidence", "Act fb"),
                Result: new StarComponentEvaluation(80, true, "Res evidence", "Res fb"),
                MissingElements: [],
                Strengths: ["Strong technical depth"],
                CoachingTips: [])));

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        // Technical interview session
        var interviewId = await StartInterviewAsync(client, "technical", "incident-test-1");
        await ProcessJobsAsync(factory);

        var interview = await GetInterviewAsync(client, interviewId);
        var firstQuestion = interview.GetProperty("questions").EnumerateArray().First();
        var questionId = firstQuestion.GetProperty("id").GetGuid();

        // Submit answer
        using var answerRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new
            {
                questionId,
                content = "Dependency injection is a design pattern in which an object receives other objects that it depends on.",
                durationSeconds = 60
            })
        };
        answerRequest.Headers.Add("Idempotency-Key", "answer-incident-test-1");
        using var answerResponse = await client.SendAsync(answerRequest);

        // Previously returned HTTP 503 AI_OUTPUT_INVALID! Now must succeed:
        Assert.True(answerResponse.IsSuccessStatusCode, await answerResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, answerResponse.StatusCode);

        var answerData = await DataAsync(answerResponse);
        Assert.True(answerData.TryGetProperty("answer", out var answerElement));
        var evalElement = answerElement.GetProperty("evaluation");
        var starElement = evalElement.GetProperty("star");
        // Authoritatively normalized to applicable = false:
        Assert.False(starElement.GetProperty("applicable").GetBoolean());
    }

    [Fact]
    public async Task FollowupFailureDoesNotDiscardValidAnswerFallbackGenerated()
    {
        var aiProvider = new TestAiProvider();
        // Valid answer evaluation
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Good answer."),
                new RubricScore("structure", 80, "Clear structure."),
                new RubricScore("completeness", 80, "Complete response."),
                new RubricScore("clarity", 80, "Clear communication.")
            ],
            "Good job.",
            new StarEvaluation(false, null, null, null, null, null, [], [], [])));

        // Followup generation throws AI provider unavailable
        aiProvider.EnqueueResponse("interview.followup",
            new AiProviderException(AiProviderFailureKind.Unavailable, "Gemini follow-up timeout simulated"));

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "technical", "followup-fail-test");
        await ProcessJobsAsync(factory);

        var interview = await GetInterviewAsync(client, interviewId);
        var questionId = interview.GetProperty("questions").EnumerateArray().First().GetProperty("id").GetGuid();

        using var answerRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new
            {
                questionId,
                content = "Solid principles help build maintainable software architecture.",
                durationSeconds = 45
            })
        };
        answerRequest.Headers.Add("Idempotency-Key", "answer-followup-fail-1");
        using var answerResponse = await client.SendAsync(answerRequest);

        // Answer MUST NOT be discarded; response must succeed!
        Assert.True(answerResponse.IsSuccessStatusCode, await answerResponse.Content.ReadAsStringAsync());
        var data = await DataAsync(answerResponse);

        // Answer is present
        Assert.True(data.TryGetProperty("answer", out _));
        // Next question is generated via fallback
        Assert.True(data.TryGetProperty("nextQuestion", out var nextQuestionElement));
        var nextQuestionContent = nextQuestionElement.GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(nextQuestionContent));
        Assert.True(nextQuestionContent!.Length <= 2_000);

        // Verify in DB that answer was persisted
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persistedAnswer = await db.InterviewAnswers.SingleOrDefaultAsync(a => a.QuestionId == questionId);
        Assert.NotNull(persistedAnswer);
    }

    [Fact]
    public async Task SemanticRepairOnAttempt1RecoversOnAttempt2ExactlyTwoCalls()
    {
        var aiProvider = new TestAiProvider();
        // Attempt 1: invalid rubric (missing clarity)
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Good answer."),
                new RubricScore("structure", 80, "Clear structure."),
                new RubricScore("completeness", 80, "Complete response.")
            ],
            "Good job.",
            new StarEvaluation(false, null, null, null, null, null, [], [], [])));

        // Attempt 2: valid rubric (all 4 criteria)
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Good answer."),
                new RubricScore("structure", 80, "Clear structure."),
                new RubricScore("completeness", 80, "Complete response."),
                new RubricScore("clarity", 85, "Very clear.")
            ],
            "Good job.",
            new StarEvaluation(false, null, null, null, null, null, [], [], [])));

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "technical", "repair-test");
        await ProcessJobsAsync(factory);

        var interview = await GetInterviewAsync(client, interviewId);
        var questionId = interview.GetProperty("questions").EnumerateArray().First().GetProperty("id").GetGuid();

        using var answerRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new
            {
                questionId,
                content = "Polymorphism enables treating objects of different types through a common interface.",
                durationSeconds = 45
            })
        };
        answerRequest.Headers.Add("Idempotency-Key", "answer-repair-1");
        using var answerResponse = await client.SendAsync(answerRequest);

        Assert.True(answerResponse.IsSuccessStatusCode, await answerResponse.Content.ReadAsStringAsync());
        // Exactly 2 calls made for interview.evaluate (1 initial + 1 repair)
        Assert.Equal(2, aiProvider.GetCallCount("interview.evaluate"));
    }

    [Fact]
    public async Task PersistentSemanticFailureCapsAtTwoAttemptsAndDoesNotPersistAnswer()
    {
        var aiProvider = new TestAiProvider();
        // Both attempts return invalid rubric
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [new RubricScore("correctness", 80, "Good answer.")],
            "Incomplete rubric",
            new StarEvaluation(false, null, null, null, null, null, [], [], [])));
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [new RubricScore("correctness", 80, "Good answer.")],
            "Still incomplete rubric",
            new StarEvaluation(false, null, null, null, null, null, [], [], [])));

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "technical", "capped-test");
        await ProcessJobsAsync(factory);

        var interview = await GetInterviewAsync(client, interviewId);
        var questionId = interview.GetProperty("questions").EnumerateArray().First().GetProperty("id").GetGuid();

        using var answerRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new
            {
                questionId,
                content = "Encapsulation bundles data with the methods that operate on that data.",
                durationSeconds = 30
            })
        };
        answerRequest.Headers.Add("Idempotency-Key", "answer-capped-1");
        using var answerResponse = await client.SendAsync(answerRequest);

        // Must fail with BadGateway or ServiceUnavailable (ExternalFailure)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, answerResponse.StatusCode);
        // Capped at exactly 2 calls!
        Assert.Equal(2, aiProvider.GetCallCount("interview.evaluate"));

        // Verify in DB that no answer was persisted
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var answerCount = await db.InterviewAnswers.CountAsync(a => a.QuestionId == questionId);
        Assert.Equal(0, answerCount);
    }

    private static async Task<Guid> StartInterviewAsync(HttpClient client, string interviewType, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new
            {
                role = "Software Engineer",
                seniority = "senior",
                interviewType,
                difficulty = "hard"
            })
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetInterviewAsync(HttpClient client, Guid interviewId)
    {
        using var response = await client.GetAsync($"/api/v1/interviews/{interviewId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await DataAsync(response);
    }

    private static async Task ProcessJobsAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>().ProcessPendingAsync(CancellationToken.None);
    }

    private static async Task SeedEntitlementAsync(NexoraApiFactory factory, Guid userId, int quota)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"practice-{Guid.NewGuid():N}", Name = "Practice test", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 1, Currency = "VND", DurationDays = 30, InterviewQuota = quota, IsActive = true, CreatedAt = now };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanPriceId = price.Id,
            PlanCodeSnapshot = plan.Code,
            AmountMinor = 1,
            Currency = "VND",
            DurationDays = 30,
            InterviewQuota = quota,
            Status = BillingValues.Fulfilled,
            PaymentProvider = "test",
            ProviderTransactionId = Guid.NewGuid().ToString("N"),
            CheckoutUrl = "https://example.test",
            CreatedAt = now,
            UpdatedAt = now
        };
        var subscription = new Subscription { Id = Guid.NewGuid(), UserId = userId, OrderId = order.Id, Status = BillingValues.Active, StartsAt = now.AddMinutes(-1), EndsAt = now.AddDays(30), CreatedAt = now, UpdatedAt = now };
        var entitlement = new Entitlement
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SubscriptionId = subscription.Id,
            PlanCodeSnapshot = plan.Code,
            Status = BillingValues.Active,
            InterviewLimit = quota,
            StartsAt = subscription.StartsAt,
            EndsAt = subscription.EndsAt,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        db.AddRange(plan, price, order, subscription, entitlement);
        await db.SaveChangesAsync();
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"ai-test-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "AI Test Candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await DataAsync(response);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
