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

[Collection("PostgreSQL primary resume")]
public sealed class AsyncInterviewApiTests
{
    [PostgresFact]
    public async Task ConcurrentAnswerReplayReleasesExactlyOneNextQuestionOnPostgres()
    {
        var aiProvider = new TestAiProvider();
        using var factory = NexoraApiFactory.CreatePostgres(
            Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!, aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "async-postgres@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "async-postgres-start");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var questionId = active.GetProperty("questions").EnumerateArray().Single().GetProperty("id").GetGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(3, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
            Assert.Equal(1, await db.InterviewQuestions.CountAsync(item =>
                item.InterviewSessionId == interviewId && item.ReleasedAt != null));
        }

        var callsBeforeAnswer = aiProvider.TotalCalls;
        var firstTask = SubmitAnswerAsync(client, interviewId, questionId, "Câu trả lời idempotent.", "async-postgres-answer");
        var secondTask = SubmitAnswerAsync(client, interviewId, questionId, "Câu trả lời idempotent.", "async-postgres-answer");
        using var first = await firstTask;
        using var second = await secondTask;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            (await DataAsync(first)).GetProperty("answer").GetProperty("id").GetGuid(),
            (await DataAsync(second)).GetProperty("answer").GetProperty("id").GetGuid());
        Assert.Equal(callsBeforeAnswer, aiProvider.TotalCalls);

        using var verifyScope = factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var answerId = await verifyDb.InterviewAnswers
            .Where(item => item.InterviewSessionId == interviewId)
            .Select(item => item.Id)
            .SingleAsync();
        Assert.Equal(1, await verifyDb.OutboxEvents.CountAsync(item =>
            item.Type == "InterviewAnswerEvaluationRequested" && item.AggregateId == answerId));
        Assert.Equal(2, await verifyDb.InterviewQuestions.CountAsync(item =>
            item.InterviewSessionId == interviewId && item.ReleasedAt != null));
        Assert.Equal(2, (await GetInterviewAsync(client, interviewId)).GetProperty("questions").GetArrayLength());
    }

    [PostgresFact]
    public async Task FailedQuestionPlanCanBeRetriedOnPostgres()
    {
        var aiProvider = new TestAiProvider();
        using var factory = NexoraApiFactory.CreatePostgres(
            Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!, aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "async-plan-postgres@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "async-plan-postgres-start");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        using (var firstAnswer = await SubmitAnswerAsync(client, interviewId, q1, "Primary answer one.", "async-plan-postgres-a1"))
        {
            var q2 = (await DataAsync(firstAnswer)).GetProperty("nextQuestion").GetProperty("id").GetGuid();
            using var secondAnswer = await SubmitAnswerAsync(client, interviewId, q2, "Primary answer two.", "async-plan-postgres-a2");
            var q3 = (await DataAsync(secondAnswer)).GetProperty("nextQuestion").GetProperty("id").GetGuid();
            using var thirdAnswer = await SubmitAnswerAsync(client, interviewId, q3, "Primary answer three.", "async-plan-postgres-a3");
            Assert.Equal(JsonValueKind.Null, (await DataAsync(thirdAnswer)).GetProperty("nextQuestion").ValueKind);
        }
        await SeedQuestionEntitlementAsync(factory, account.UserId, questionLimit: 6);

        using (var continueRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue"))
        {
            continueRequest.Headers.Add("Idempotency-Key", "async-plan-postgres-continue");
            using var continueResponse = await client.SendAsync(continueRequest);
            Assert.Equal(HttpStatusCode.OK, continueResponse.StatusCode);
        }

        aiProvider.EnqueueResponse(AiPurposes.InterviewFirstQuestion,
            new AiProviderException(AiProviderFailureKind.Unavailable, "postgres question plan failure"));
        aiProvider.EnqueueResponse(AiPurposes.InterviewFirstQuestion,
            new AiProviderException(AiProviderFailureKind.Unavailable, "postgres question plan failure retry"));
        await ProcessJobsAsync(factory);
        Assert.Equal(InterviewQuestionPreparationStates.Failed,
            (await GetInterviewAsync(client, interviewId)).GetProperty("questionPreparationState").GetString());

        using (var retryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/questions/retry"))
        {
            retryRequest.Headers.Add("Idempotency-Key", "async-plan-postgres-retry");
            using var retryResponse = await client.SendAsync(retryRequest);
            Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        }
        await ProcessJobsAsync(factory);

        var recovered = await GetInterviewAsync(client, interviewId);
        Assert.Equal(InterviewQuestionPreparationStates.Ready,
            recovered.GetProperty("questionPreparationState").GetString());
        Assert.Contains(recovered.GetProperty("questions").EnumerateArray(),
            item => item.GetProperty("sequence").GetInt32() == 4);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(6, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
        Assert.Equal(2, await db.OutboxEvents.CountAsync(item =>
            item.Type == "InterviewQuestionPlanRequested" && item.AggregateId == interviewId));
    }

    [Fact]
    public async Task AnswerPersistsAndReleasesPreparedQuestionWithoutCallingAiSynchronously()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "async-answer@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "async-answer-start");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions").EnumerateArray().Single().GetProperty("id").GetGuid();
        var callsBeforeAnswer = aiProvider.TotalCalls;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new { questionId = q1, content = "Tôi đã xử lý sự cố bằng dữ liệu đo được.", durationSeconds = 30 })
        };
        request.Headers.Add("Idempotency-Key", "async-answer-one");

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = await DataAsync(response);
        var answer = data.GetProperty("answer");
        Assert.Equal("queued", answer.GetProperty("evaluationState").GetString());
        Assert.Equal(JsonValueKind.Null, answer.GetProperty("evaluation").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, data.GetProperty("nextQuestion").ValueKind);
        Assert.Equal(callsBeforeAnswer, aiProvider.TotalCalls);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.InterviewAnswers.CountAsync(item => item.InterviewSessionId == interviewId));
        Assert.Equal(1, await db.OutboxEvents.CountAsync(item =>
            item.Type == "InterviewAnswerEvaluationRequested" && item.AggregateId == answer.GetProperty("id").GetGuid()));

        var refreshed = await GetInterviewAsync(client, interviewId);
        Assert.Equal(2, refreshed.GetProperty("questions").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, refreshed.GetProperty("answers")[0].GetProperty("evaluation").ValueKind);
        Assert.Equal(1, refreshed.GetProperty("evaluationProgress").GetProperty("queued").GetInt32());
    }

    [Fact]
    public async Task WorkerStoresEvaluationButActiveInterviewStillHidesItsPayload()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "async-worker@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "async-worker-start");
        await ProcessJobsAsync(factory);
        var questionId = (await GetInterviewAsync(client, interviewId)).GetProperty("questions").EnumerateArray().Single().GetProperty("id").GetGuid();

        using var answerResponse = await SubmitAnswerAsync(client, interviewId, questionId, "Tôi đã cải thiện độ trễ.", "async-worker-answer");
        var answerId = (await DataAsync(answerResponse)).GetProperty("answer").GetProperty("id").GetGuid();
        await ProcessJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persisted = await db.InterviewAnswers.SingleAsync(item => item.Id == answerId);
        Assert.Equal(InterviewAnswerEvaluationStates.Ready, persisted.EvaluationStatus);
        Assert.False(string.IsNullOrWhiteSpace(persisted.Evaluation));

        var active = await GetInterviewAsync(client, interviewId);
        var publicAnswer = active.GetProperty("answers").EnumerateArray().Single();
        Assert.Equal("ready", publicAnswer.GetProperty("evaluationState").GetString());
        Assert.Equal(JsonValueKind.Null, publicAnswer.GetProperty("evaluation").ValueKind);
    }

    [Fact]
    public async Task ProviderFailureMarksAcceptedAnswerFailedAndResultsRetryRequeuesOnlyThatAnswer()
    {
        var aiProvider = new TestAiProvider();
        aiProvider.EnqueueResponse(AiOperations.InterviewEvaluate.Purpose,
            new AiProviderException(AiProviderFailureKind.Unavailable, "test failure"));
        aiProvider.EnqueueResponse(AiOperations.InterviewEvaluate.Purpose,
            new AiProviderException(AiProviderFailureKind.Unavailable, "test failure"));
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "async-failure@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "async-failure-start");
        await ProcessJobsAsync(factory);
        var questionId = (await GetInterviewAsync(client, interviewId)).GetProperty("questions").EnumerateArray().Single().GetProperty("id").GetGuid();

        using var answer = await SubmitAnswerAsync(client, interviewId, questionId, "Tôi xác định nguyên nhân và xử lý.", "async-failure-answer");
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        var secondQuestionId = (await DataAsync(answer)).GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await ProcessJobsAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var saved = await db.InterviewAnswers.SingleAsync(item => item.QuestionId == questionId);
            Assert.Equal(InterviewAnswerEvaluationStates.Failed, saved.EvaluationStatus);
            Assert.Equal("AI_PROVIDER_UNAVAILABLE", saved.EvaluationErrorCode);
            Assert.Null(saved.Evaluation);
        }

        var failedView = await GetInterviewAsync(client, interviewId);
        Assert.Equal(PracticeValues.Active, failedView.GetProperty("status").GetString());
        Assert.Equal(InterviewResultStates.Collecting, failedView.GetProperty("resultState").GetString());
        Assert.Equal(JsonValueKind.Null, failedView.GetProperty("answers")[0].GetProperty("evaluation").ValueKind);

        using var secondAnswer = await SubmitAnswerAsync(
            client,
            interviewId,
            secondQuestionId,
            "Tôi theo dõi kết quả và ghi nhận bài học.",
            "async-failure-answer-two");
        Assert.Equal(HttpStatusCode.OK, secondAnswer.StatusCode);
        await ProcessJobsAsync(factory);
        using (var complete = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/complete"))
        {
            complete.Headers.Add("Idempotency-Key", "async-failure-complete");
            using var completeResponse = await client.SendAsync(complete);
            Assert.Equal(HttpStatusCode.Accepted, completeResponse.StatusCode);
        }

        var completingFailed = await GetInterviewAsync(client, interviewId);
        Assert.Equal(PracticeValues.Completing, completingFailed.GetProperty("status").GetString());
        Assert.Equal(InterviewResultStates.Failed, completingFailed.GetProperty("resultState").GetString());

        var callsBeforeRetry = aiProvider.GetCallCount(AiOperations.InterviewEvaluate.Purpose);
        using var retry = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/results/retry");
        retry.Headers.Add("Idempotency-Key", "async-failure-retry");
        using var retryResponse = await client.SendAsync(retry);
        Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
        await ProcessJobsAsync(factory);
        Assert.Equal(callsBeforeRetry + 1, aiProvider.GetCallCount(AiOperations.InterviewEvaluate.Purpose));

        using var scopeAfterRetry = factory.Services.CreateScope();
        var dbAfterRetry = scopeAfterRetry.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(InterviewAnswerEvaluationStates.Ready,
            (await dbAfterRetry.InterviewAnswers.SingleAsync(item => item.QuestionId == questionId)).EvaluationStatus);
    }

    [Fact]
    public async Task UnexpectedAnswerEvaluationFailureUsesInternalProvenanceCode()
    {
        var aiProvider = new TestAiProvider();
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, new InvalidOperationException("unexpected processing failure"));
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, new InvalidOperationException("unexpected processing failure retry"));
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "async-internal-failure@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "async-internal-failure-start");
        await ProcessJobsAsync(factory);
        var questionId = (await GetInterviewAsync(client, interviewId)).GetProperty("questions")[0].GetProperty("id").GetGuid();

        using var answer = await SubmitAnswerAsync(
            client,
            interviewId,
            questionId,
            "Tôi phân tích nguyên nhân và theo dõi kết quả.",
            "async-internal-failure-answer");
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        await ProcessJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persisted = await db.InterviewAnswers.SingleAsync(item => item.QuestionId == questionId);
        Assert.Equal(InterviewAnswerEvaluationStates.Failed, persisted.EvaluationStatus);
        Assert.Equal("INTERNAL_PROCESSING_FAILED", persisted.EvaluationErrorCode);
        Assert.Null(persisted.Evaluation);
    }

    private static async Task<HttpResponseMessage> SubmitAnswerAsync(
        HttpClient client, Guid interviewId, Guid questionId, string content, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new { questionId, content, durationSeconds = 30 })
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<Guid> StartInterviewAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new { role = "Backend Engineer", seniority = "senior", interviewType = "technical", difficulty = "medium" })
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

    private static async Task SeedQuestionEntitlementAsync(
        NexoraApiFactory factory,
        Guid userId,
        int questionLimit)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Code = $"pg-{Guid.NewGuid():N}",
            Name = "PostgreSQL question retry plan",
            IsActive = true,
            CreatedAt = now
        };
        var price = new PlanPrice
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            AmountMinor = 1,
            Currency = "VND",
            DurationDays = 30,
            InterviewQuota = 1,
            IsActive = true,
            CreatedAt = now
        };
        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanPriceId = price.Id,
            PlanCodeSnapshot = plan.Code,
            AmountMinor = 1,
            Currency = "VND",
            DurationDays = 30,
            InterviewQuota = 1,
            Status = BillingValues.Fulfilled,
            PaymentProvider = "test",
            ProviderTransactionId = Guid.NewGuid().ToString("N"),
            CheckoutUrl = "https://example.test",
            CreatedAt = now,
            UpdatedAt = now
        };
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrderId = order.Id,
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
            PlanCodeSnapshot = plan.Code,
            Status = BillingValues.Active,
            InterviewLimit = 1,
            StartsAt = subscription.StartsAt,
            EndsAt = subscription.EndsAt,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        db.AddRange(plan, price, order, subscription, entitlement);
        var definition = await db.FeatureDefinitions.SingleAsync(item => item.Code == FeatureValues.InterviewQuestionLimit);
        db.EntitlementFeatures.Add(new EntitlementFeature
        {
            Id = Guid.NewGuid(),
            EntitlementId = entitlement.Id,
            FeatureDefinitionId = definition.Id,
            FeatureCode = definition.Code,
            IsEnabled = true,
            Limit = questionLimit,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Account> RegisterAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Async tester"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
