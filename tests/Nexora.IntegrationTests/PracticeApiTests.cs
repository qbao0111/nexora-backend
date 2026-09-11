using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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

public sealed class PracticeApiTests
{
    private const string TestWebhookKey = "phase2-test-webhook-key-material";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task FreeInterviewStopsAfterThreeCanonicalPrimariesWithUpgradeContinuation()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "a7-free-canonical");
        await ProcessJobsAsync(factory);

        var interview = await GetInterviewAsync(client, interviewId);
        var q1 = interview.GetProperty("questions")[0];
        Assert.Equal(InterviewQuestionValues.Primary, q1.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.SelfIntroduction, q1.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, q1.GetProperty("parentQuestionId").ValueKind);
        Assert.Contains("[self_introduction]", q1.GetProperty("content").GetString(), StringComparison.Ordinal);

        var answer1 = await AnswerAsync(client, interviewId, q1.GetProperty("id").GetGuid(), "Tôi giới thiệu kinh nghiệm của mình.", "a7-free-a1");
        var q2 = answer1.GetProperty("nextQuestion");
        var afterFirstContinuation = answer1.GetProperty("continuation");
        Assert.Equal(InterviewContinuationValues.InProgress, afterFirstContinuation.GetProperty("state").GetString());
        Assert.False(afterFirstContinuation.GetProperty("canFinishNow").GetBoolean());
        Assert.False(afterFirstContinuation.GetProperty("canUpgradeAndContinue").GetBoolean());
        Assert.Equal(InterviewQuestionValues.Primary, q2.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.BehavioralStar, q2.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, q2.GetProperty("parentQuestionId").ValueKind);
        Assert.Contains("[behavioral_star]", q2.GetProperty("content").GetString(), StringComparison.Ordinal);

        var answer2 = await AnswerAsync(client, interviewId, q2.GetProperty("id").GetGuid(), "Tôi đã xử lý một tình huống khó.", "a7-free-a2");
        var q3 = answer2.GetProperty("nextQuestion");
        Assert.Equal(InterviewQuestionValues.Primary, q3.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.MotivationRoleFit, q3.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, q3.GetProperty("parentQuestionId").ValueKind);
        Assert.False(answer2.GetProperty("isComplete").GetBoolean());
        Assert.Equal(InterviewContinuationValues.InProgress, answer2.GetProperty("continuation").GetProperty("state").GetString());
        Assert.True(answer2.GetProperty("continuation").GetProperty("canFinishNow").GetBoolean());
        Assert.False(answer2.GetProperty("continuation").GetProperty("canUpgradeAndContinue").GetBoolean());
        Assert.Contains("[motivation_role_fit]", q3.GetProperty("content").GetString(), StringComparison.Ordinal);

        var answer3 = await AnswerAsync(client, interviewId, q3.GetProperty("id").GetGuid(), "Tôi muốn tạo giá trị ở vai trò này.", "a7-free-a3");
        Assert.Equal(JsonValueKind.Null, answer3.GetProperty("nextQuestion").ValueKind);
        Assert.False(answer3.GetProperty("isComplete").GetBoolean());
        var continuation = answer3.GetProperty("continuation");
        Assert.Equal(InterviewContinuationValues.UpgradeRequired, continuation.GetProperty("state").GetString());
        Assert.True(continuation.GetProperty("canFinishNow").GetBoolean());
        Assert.True(continuation.GetProperty("canUpgradeAndContinue").GetBoolean());

        using var continueRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        continueRequest.Headers.Add("Idempotency-Key", "a7-free-continue");
        using var continueResponse = await client.SendAsync(continueRequest);
        Assert.Equal(HttpStatusCode.Forbidden, continueResponse.StatusCode);
        Assert.Contains("INTERVIEW_UPGRADE_REQUIRED", await continueResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, aiProvider.GetCallCount(AiPurposes.InterviewFollowup));
    }

    [Fact]
    public async Task ConfirmedVoiceTextUsesTheCanonicalAnswerPathAndIsIdempotent()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "a10-confirmed-text-start");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestion = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        const string rawTranscript = "I used Redis and reduced latency by 70 percent.";
        const string confirmedText = "I used PostgreSQL and improved reliability.";

        var firstResult = await AnswerAsync(client, interviewId, firstQuestion, $"  {confirmedText}  ", "a10-confirmed-text-answer");
        var savedAnswer = firstResult.GetProperty("answer");
        var answerId = savedAnswer.GetProperty("id").GetGuid();
        Assert.Equal(confirmedText, savedAnswer.GetProperty("content").GetString());

        var evaluationInvocation = aiProvider.Invocations
            .Single(item => item.Purpose == AiPurposes.InterviewEvaluate);
        Assert.Contains(confirmedText, evaluationInvocation.UntrustedInput, StringComparison.Ordinal);
        Assert.DoesNotContain(rawTranscript, evaluationInvocation.UntrustedInput, StringComparison.Ordinal);
        Assert.Equal(1, aiProvider.GetCallCount(AiPurposes.InterviewEvaluate));

        var replay = await AnswerAsync(client, interviewId, firstQuestion, $"  {confirmedText}  ", "a10-confirmed-text-answer");
        Assert.Equal(answerId, replay.GetProperty("answer").GetProperty("id").GetGuid());
        Assert.Equal(1, aiProvider.GetCallCount(AiPurposes.InterviewEvaluate));

        var secondQuestion = firstResult.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, secondQuestion, "I documented the rollout and monitored the result.", "a10-confirmed-text-answer-two");
        await CompleteAsync(client, interviewId, "a10-confirmed-text-complete");
        await ProcessJobsAsync(factory);

        var reportInvocation = aiProvider.Invocations
            .Single(item => item.Purpose == AiPurposes.InterviewReport);
        Assert.Contains(confirmedText, reportInvocation.UntrustedInput, StringComparison.Ordinal);
        Assert.DoesNotContain(rawTranscript, reportInvocation.UntrustedInput, StringComparison.Ordinal);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persisted = await db.InterviewAnswers.SingleAsync(item => item.Id == answerId);
        Assert.Equal(confirmedText, persisted.Content);
        Assert.Equal(1, await db.InterviewAnswers.CountAsync(item => item.InterviewSessionId == interviewId && item.QuestionId == firstQuestion));
    }

    [Fact]
    public async Task PaidContinuationUsesSameSessionAndIdempotentQuestion()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "a7-paid-start", "technical");
        await ProcessJobsAsync(factory);
        var interview = await GetInterviewAsync(client, interviewId);
        var q1 = interview.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var a1 = await AnswerAsync(client, interviewId, q1, "Giới thiệu kinh nghiệm.", "a7-paid-a1");
        var q2 = a1.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var a2 = await AnswerAsync(client, interviewId, q2, "Một tình huống tôi đã xử lý.", "a7-paid-a2");
        var q3 = a2.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var a3 = await AnswerAsync(client, interviewId, q3, "Tôi phù hợp với vai trò.", "a7-paid-a3");
        Assert.Equal(JsonValueKind.Null, a3.GetProperty("nextQuestion").ValueKind);
        var firstQuestionCallsBeforeContinuation = aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion);

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        firstRequest.Headers.Add("Idempotency-Key", "a7-paid-continue");
        using var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await DataAsync(firstResponse);
        Assert.Equal(interviewId, first.GetProperty("id").GetGuid());
        var q4 = first.GetProperty("questions").EnumerateArray().Single(item => item.GetProperty("sequence").GetInt32() == 4);
        Assert.Equal(InterviewQuestionValues.Primary, q4.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.Technical, q4.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, q4.GetProperty("parentQuestionId").ValueKind);
        Assert.Contains("[technical]", q4.GetProperty("content").GetString(), StringComparison.Ordinal);

        using var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        replayRequest.Headers.Add("Idempotency-Key", "a7-paid-continue");
        using var replayResponse = await client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        var replay = await DataAsync(replayResponse);
        Assert.Equal(first.GetProperty("questions").GetArrayLength(), replay.GetProperty("questions").GetArrayLength());
        Assert.Equal(1, aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion) - firstQuestionCallsBeforeContinuation);

        var q4Answer = await AnswerAsync(client, interviewId, q4.GetProperty("id").GetGuid(), "A measurable technical result.", "a7-paid-a4");
        Assert.False(q4Answer.GetProperty("isComplete").GetBoolean());
        Assert.Equal(InterviewContinuationValues.InProgress, q4Answer.GetProperty("continuation").GetProperty("state").GetString());
        Assert.True(q4Answer.GetProperty("continuation").GetProperty("canFinishNow").GetBoolean());

        using var fifthRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        fifthRequest.Headers.Add("Idempotency-Key", "a7-paid-continue-q5");
        using var fifthResponse = await client.SendAsync(fifthRequest);
        Assert.Equal(HttpStatusCode.OK, fifthResponse.StatusCode);
        var fifth = await DataAsync(fifthResponse);
        var q5 = fifth.GetProperty("questions").EnumerateArray().Single(item => item.GetProperty("sequence").GetInt32() == 5);
        Assert.Equal(InterviewQuestionValues.Primary, q5.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.Technical, q5.GetProperty("topic").GetString());
        Assert.Contains("[technical]", q5.GetProperty("content").GetString(), StringComparison.Ordinal);

        var q5Answer = await AnswerAsync(client, interviewId, q5.GetProperty("id").GetGuid(), "Another measurable technical result.", "a7-paid-a5");
        Assert.False(q5Answer.GetProperty("isComplete").GetBoolean());
        Assert.Equal(InterviewContinuationValues.InProgress, q5Answer.GetProperty("continuation").GetProperty("state").GetString());
        Assert.True(q5Answer.GetProperty("continuation").GetProperty("canFinishNow").GetBoolean());
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion) - firstQuestionCallsBeforeContinuation);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persistedQuestions = await db.InterviewQuestions.Where(item => item.InterviewSessionId == interviewId).ToListAsync();
        Assert.Equal(5, persistedQuestions.Count);
        Assert.All(persistedQuestions, item =>
        {
            Assert.Equal(AiOperations.InterviewFirstQuestion.PromptVersion, item.PromptVersion);
            Assert.Equal(aiProvider.ModelVersion, item.ModelVersion);
        });
    }

    [Fact]
    public async Task VerifiedCheckoutUnlocksPaidContinuationWithoutChargingAnotherInterview()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "a7-checkout-start", "technical");
        await ProcessJobsAsync(factory);
        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var q2 = (await AnswerAsync(client, interviewId, q1, "Primary answer one.", "a7-checkout-a1"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3 = (await AnswerAsync(client, interviewId, q2, "Primary answer two.", "a7-checkout-a2"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var freeResult = await AnswerAsync(client, interviewId, q3, "Primary answer three.", "a7-checkout-a3");
        Assert.Equal(InterviewContinuationValues.UpgradeRequired, freeResult.GetProperty("continuation").GetProperty("state").GetString());

        var paidPlan = await SeedPaidContinuationPlanAsync(factory, questionLimit: 6);
        var orderId = await CreateCheckoutAsync(client, paidPlan.PriceId, "a7-checkout-paid");
        var webhook = await CreateFakeWebhookAsync(factory, orderId, "a7-checkout-paid-event");
        using (var webhookRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/fake")
        {
            Content = new ByteArrayContent(webhook.Body)
        })
        {
            webhookRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            webhookRequest.Headers.Add("X-Payment-Timestamp", webhook.Timestamp);
            webhookRequest.Headers.Add("X-Payment-Signature", webhook.Signature);
            using var webhookResponse = await client.SendAsync(webhookRequest);
            Assert.Equal(HttpStatusCode.NoContent, webhookResponse.StatusCode);
        }

        using var continueRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        continueRequest.Headers.Add("Idempotency-Key", "a7-checkout-continue");
        using var continueResponse = await client.SendAsync(continueRequest);
        Assert.Equal(HttpStatusCode.OK, continueResponse.StatusCode);
        var continued = await DataAsync(continueResponse);
        Assert.Equal(interviewId, continued.GetProperty("id").GetGuid());
        var q4 = continued.GetProperty("questions").EnumerateArray().Single(item => item.GetProperty("sequence").GetInt32() == 4);
        Assert.Equal(InterviewQuestionValues.Primary, q4.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.Technical, q4.GetProperty("topic").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var session = await db.InterviewSessions.SingleAsync(item => item.Id == interviewId);
        Assert.Equal(4, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Reserve));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Consume));
        var originalReservation = await db.UsageEvents.SingleAsync(item => item.Id == session.ReservationEventId);
        Assert.Equal(BillingValues.Reserve, originalReservation.Action);
        Assert.Equal(interviewId.ToString("N"), originalReservation.SourceId);

        var paidEntitlement = await db.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot == paidPlan.PlanCode);
        Assert.Equal(0, paidEntitlement.Reserved);
        Assert.Equal(0, paidEntitlement.Consumed);
        var paidQuestionLimit = await db.EntitlementFeatures.Include(item => item.Entitlement)
            .SingleAsync(item => item.EntitlementId == paidEntitlement.Id && item.FeatureCode == FeatureValues.InterviewQuestionLimit);
        Assert.Equal(6, paidQuestionLimit.Limit);
    }

    [Fact]
    public async Task ConfiguredPaidLimitStopsAtTerminalCapWithoutExtraGenerationOrUsage()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "a7-paid-cap-start", "technical");
        await ProcessJobsAsync(factory);
        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var q2 = (await AnswerAsync(client, interviewId, q1, "Primary answer one.", "a7-paid-cap-a1"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3 = (await AnswerAsync(client, interviewId, q2, "Primary answer two.", "a7-paid-cap-a2"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var freeResult = await AnswerAsync(client, interviewId, q3, "Primary answer three.", "a7-paid-cap-a3");
        Assert.Equal(InterviewContinuationValues.UpgradeRequired, freeResult.GetProperty("continuation").GetProperty("state").GetString());

        var paidPlan = await SeedPaidContinuationPlanAsync(factory, questionLimit: 5);
        var orderId = await CreateCheckoutAsync(client, paidPlan.PriceId, "a7-paid-cap-checkout");
        var webhook = await CreateFakeWebhookAsync(factory, orderId, "a7-paid-cap-event");
        using (var webhookRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/fake")
        {
            Content = new ByteArrayContent(webhook.Body)
        })
        {
            webhookRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            webhookRequest.Headers.Add("X-Payment-Timestamp", webhook.Timestamp);
            webhookRequest.Headers.Add("X-Payment-Signature", webhook.Signature);
            using var webhookResponse = await client.SendAsync(webhookRequest);
            Assert.Equal(HttpStatusCode.NoContent, webhookResponse.StatusCode);
        }

        using var q4Request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        q4Request.Headers.Add("Idempotency-Key", "a7-paid-cap-q4");
        using var q4Response = await client.SendAsync(q4Request);
        Assert.Equal(HttpStatusCode.OK, q4Response.StatusCode);
        var q4 = (await DataAsync(q4Response)).GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("sequence").GetInt32() == 4);
        var q4Answer = await AnswerAsync(client, interviewId, q4.GetProperty("id").GetGuid(), "Paid answer four.", "a7-paid-cap-a4");
        Assert.Equal(InterviewContinuationValues.InProgress, q4Answer.GetProperty("continuation").GetProperty("state").GetString());

        using var q5Request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        q5Request.Headers.Add("Idempotency-Key", "a7-paid-cap-q5");
        using var q5Response = await client.SendAsync(q5Request);
        Assert.Equal(HttpStatusCode.OK, q5Response.StatusCode);
        var q5 = (await DataAsync(q5Response)).GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("sequence").GetInt32() == 5);
        var q5Answer = await AnswerAsync(client, interviewId, q5.GetProperty("id").GetGuid(), "Paid answer five.", "a7-paid-cap-a5");
        Assert.True(q5Answer.GetProperty("isComplete").GetBoolean());
        Assert.Equal(InterviewContinuationValues.MaxQuestionsReached, q5Answer.GetProperty("continuation").GetProperty("state").GetString());
        Assert.True(q5Answer.GetProperty("continuation").GetProperty("canFinishNow").GetBoolean());

        var firstQuestionCallsAtCap = aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion);
        using var overCapRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        overCapRequest.Headers.Add("Idempotency-Key", "a7-paid-cap-over");
        using var overCapResponse = await client.SendAsync(overCapRequest);
        Assert.Equal(HttpStatusCode.Conflict, overCapResponse.StatusCode);
        Assert.Contains("INTERVIEW_MAX_QUESTIONS_REACHED", await overCapResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(firstQuestionCallsAtCap, aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(5, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Reserve));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Consume));
    }

    [Fact]
    public async Task DistinctConcurrentContinuationKeysConvergeToOneQuestionWithoutExtraUsage()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "a7-concurrent-start", "technical");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var q2 = (await AnswerAsync(client, interviewId, q1, "Primary answer one.", "a7-concurrent-a1"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3 = (await AnswerAsync(client, interviewId, q2, "Primary answer two.", "a7-concurrent-a2"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, q3, "Primary answer three.", "a7-concurrent-a3");

        using var client1 = factory.CreateHttpsClient();
        client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var client2 = factory.CreateHttpsClient();
        client2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var responses = await Task.WhenAll(
            ContinueAsync(client1, interviewId, "a7-concurrent-q4-a"),
            ContinueAsync(client2, interviewId, "a7-concurrent-q4-b"));
        foreach (var response in responses)
        {
            Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
                await response.Content.ReadAsStringAsync());
            response.Dispose();
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(4, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
        Assert.Equal(1, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId && item.Sequence > InterviewQuestionValues.FreeQuestionLimit));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Reserve));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Consume));
    }

    [Fact]
    public async Task PaidBehavioralContinuationUsesFollowupOnlyForMissingStarEvidence()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "a7-paid-behavioral");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var q2 = (await AnswerAsync(client, interviewId, q1, "Giới thiệu kinh nghiệm.", "a7-paid-behavioral-a1"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3 = (await AnswerAsync(client, interviewId, q2, "Một tình huống tôi đã xử lý.", "a7-paid-behavioral-a2"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, q3, "Tôi phù hợp với vai trò.", "a7-paid-behavioral-a3");

        using var continueRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        continueRequest.Headers.Add("Idempotency-Key", "a7-paid-behavioral-q4");
        using var continueResponse = await client.SendAsync(continueRequest);
        Assert.Equal(HttpStatusCode.OK, continueResponse.StatusCode);
        var q4 = (await DataAsync(continueResponse)).GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("sequence").GetInt32() == 4);
        Assert.Equal(InterviewQuestionValues.Behavioral, q4.GetProperty("topic").GetString());
        Assert.Equal(InterviewQuestionValues.Primary, q4.GetProperty("kind").GetString());
        var q4Id = q4.GetProperty("id").GetGuid();

        await AnswerAsync(client, interviewId, q4Id, "Tôi đã phối hợp với nhóm để xử lý sự cố.", "a7-paid-behavioral-a4");

        using var followupRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        followupRequest.Headers.Add("Idempotency-Key", "a7-paid-behavioral-q5");
        using var followupResponse = await client.SendAsync(followupRequest);
        Assert.Equal(HttpStatusCode.OK, followupResponse.StatusCode);
        var q5 = (await DataAsync(followupResponse)).GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("sequence").GetInt32() == 5);
        Assert.Equal(InterviewQuestionValues.Followup, q5.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.Behavioral, q5.GetProperty("topic").GetString());
        Assert.Equal(q4Id, q5.GetProperty("parentQuestionId").GetGuid());
        Assert.Equal(1, aiProvider.GetCallCount(AiPurposes.InterviewFollowup));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persistedFollowup = await db.InterviewQuestions.SingleAsync(item => item.Id == q5.GetProperty("id").GetGuid());
        Assert.Equal(AiOperations.InterviewFollowup.PromptVersion, persistedFollowup.PromptVersion);
        Assert.Equal(aiProvider.ModelVersion, persistedFollowup.ModelVersion);
    }

    [Fact]
    public async Task ExecutableRenamedAsPdfIsRejectedWithoutFinalRecordT06()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var bytes = Encoding.ASCII.GetBytes("MZ fake executable");
        using var presign = await client.PostAsJsonAsync("/api/v1/uploads/presign", new
        {
            fileName = "malware.pdf",
            contentType = "application/pdf",
            size = bytes.Length
        });
        Assert.Equal(HttpStatusCode.OK, presign.StatusCode);
        var intent = await DataAsync(presign);
        using var upload = new ByteArrayContent(bytes);
        using var uploadResponse = await client.PutAsync(intent.GetProperty("uploadUrl").GetString(), upload);
        Assert.Equal(HttpStatusCode.BadRequest, uploadResponse.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await db.StoredFiles.CountAsync());
        Assert.Equal(0, await db.Resumes.CountAsync());
    }

    [Fact]
    public async Task AiFailureBeforeActivationAtomicallyVoidsReservationAndFailsSessionT07()
    {
        using var factory = new NexoraApiFactory(new FailingAiProvider());
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "failure-t07");

        await ProcessJobsAsync(factory);
        using var response = await client.GetAsync($"/api/v1/interviews/{interviewId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var interview = await DataAsync(response);
        Assert.Equal(PracticeValues.Failed, interview.GetProperty("status").GetString());
        Assert.Empty(interview.GetProperty("questions").EnumerateArray());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var entitlement = await db.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free");
        Assert.Equal(0, entitlement.Reserved);
        Assert.Equal(0, entitlement.Consumed);
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Void));
    }

    [Fact]
    public async Task OverlongFirstQuestionRepairsOnceThenFailsWithoutPersistingQuestion()
    {
        var aiProvider = new TestAiProvider();
        var overlong = new GeneratedQuestion(new string('x', 2_001));
        aiProvider.EnqueueResponse(AiPurposes.InterviewFirstQuestion, overlong);
        aiProvider.EnqueueResponse(AiPurposes.InterviewFirstQuestion, overlong);
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "overlong-first-question", "technical");

        await ProcessJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion));
        Assert.Equal(PracticeValues.Failed, (await db.InterviewSessions.SingleAsync(item => item.Id == interviewId)).Status);
        Assert.Equal(0, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
        var entitlement = await db.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free");
        Assert.Equal(0, entitlement.Reserved);
        Assert.Equal(0, entitlement.Consumed);
    }

    [Fact]
    public async Task AnswerRefreshCompletionReportAndDashboardRemainDurableT07T08()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "success-t07-t08");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        Assert.Equal(PracticeValues.Active, active.GetProperty("status").GetString());
        var firstQuestionView = active.GetProperty("questions")[0];
        var firstQuestion = firstQuestionView.GetProperty("id").GetGuid();
        Assert.Equal(InterviewQuestionValues.Primary, firstQuestionView.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.SelfIntroduction, firstQuestionView.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, firstQuestionView.GetProperty("parentQuestionId").ValueKind);
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestion, "Tôi phân tích nguyên nhân, phối hợp đội và giảm 30% lỗi.", "answer-one");
        var firstStar = firstAnswer.GetProperty("answer").GetProperty("evaluation").GetProperty("star");
        Assert.False(firstStar.GetProperty("applicable").GetBoolean());
        var secondQuestionView = firstAnswer.GetProperty("nextQuestion");
        var secondQuestion = secondQuestionView.GetProperty("id").GetGuid();
        Assert.Equal(InterviewQuestionValues.Primary, secondQuestionView.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.BehavioralStar, secondQuestionView.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, secondQuestionView.GetProperty("parentQuestionId").ValueKind);

        var refreshed = await GetInterviewAsync(client, interviewId);
        Assert.Equal(2, refreshed.GetProperty("questions").GetArrayLength());
        Assert.Single(refreshed.GetProperty("answers").EnumerateArray());
        var secondAnswer = await AnswerAsync(client, interviewId, secondQuestion, "Tôi sẽ đo baseline sớm hơn và kiểm tra theo tuần.", "answer-two");
        Assert.True(secondAnswer.GetProperty("answer").GetProperty("evaluation").GetProperty("star").GetProperty("applicable").GetBoolean());
        var thirdQuestionView = secondAnswer.GetProperty("nextQuestion");
        var thirdQuestion = thirdQuestionView.GetProperty("id").GetGuid();
        Assert.Equal(InterviewQuestionValues.Primary, thirdQuestionView.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.MotivationRoleFit, thirdQuestionView.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, thirdQuestionView.GetProperty("parentQuestionId").ValueKind);
        var thirdAnswer = await AnswerAsync(client, interviewId, thirdQuestion, "TÃ´i phÃ¹ há»£p vá»›i vai trÃ² nhá» kinh nghiá»‡m liÃªn quan.", "answer-three");
        Assert.Equal(JsonValueKind.Null, thirdAnswer.GetProperty("nextQuestion").ValueKind);
        Assert.False(thirdAnswer.GetProperty("isComplete").GetBoolean());
        Assert.Equal(InterviewContinuationValues.UpgradeRequired, thirdAnswer.GetProperty("continuation").GetProperty("state").GetString());
        Assert.True(thirdAnswer.GetProperty("continuation").GetProperty("canFinishNow").GetBoolean());

        using (var completeRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/complete"))
        {
            completeRequest.Headers.Add("Idempotency-Key", "complete-one");
            using var complete = await client.SendAsync(completeRequest);
            Assert.Equal(HttpStatusCode.Accepted, complete.StatusCode);
        }
        await ProcessJobsAsync(factory);
        using var reportResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report");
        var report = await DataAsync(reportResponse);
        Assert.InRange(report.GetProperty("overallScore").GetInt32(), 0, 100);
        Assert.Equal(4, report.GetProperty("rubric").GetArrayLength());
        Assert.NotEmpty(report.GetProperty("strengths").EnumerateArray());
        Assert.NotEmpty(report.GetProperty("gaps").EnumerateArray());
        Assert.NotEmpty(report.GetProperty("actionPlan").EnumerateArray());
        var sample = report.GetProperty("sample");
        Assert.Equal(3, sample.GetProperty("answeredQuestions").GetInt32());
        Assert.Equal(3, sample.GetProperty("issuedQuestions").GetInt32());
        Assert.True(sample.GetProperty("isPartial").GetBoolean());
        Assert.Contains("partial sample", report.GetProperty("disclaimer").GetString(), StringComparison.OrdinalIgnoreCase);
        var starSummary = report.GetProperty("starSummary");
        Assert.Equal(1, starSummary.GetProperty("applicableAnswers").GetInt32());
        Assert.Equal("result", starSummary.GetProperty("weakestComponent").GetString());
        Assert.Contains(starSummary.GetProperty("coachingPriorities").EnumerateArray(), item => item.GetString()!.Contains("kết quả", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("coaching", report.GetProperty("disclaimer").GetString(), StringComparison.OrdinalIgnoreCase);

        using var dashboardResponse = await client.GetAsync("/api/v1/dashboard");
        var dashboard = await DataAsync(dashboardResponse);
        Assert.Contains(dashboard.GetProperty("interviews").EnumerateArray(), item => item.GetProperty("id").GetGuid() == interviewId);
        Assert.Contains(dashboard.GetProperty("reports").EnumerateArray(), item => item.GetProperty("interviewId").GetGuid() == interviewId);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var entitlement = await db.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free");
        Assert.Equal(0, entitlement.Reserved);
        Assert.Equal(1, entitlement.Consumed);
        Assert.Equal(1, await db.InterviewReports.CountAsync(item => item.InterviewSessionId == interviewId));

        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/interviews/{interviewId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"/api/v1/interviews/{interviewId}/report")).StatusCode);
    }

    [Fact]
    public async Task DashboardSummaryPreservesContractBillingFeaturesOrderingAndOwnership()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient);
        await SeedEntitlementAsync(factory, owner.UserId, 3, questionLimit: 6);
        ownerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        var olderInterviewId = await StartInterviewAsync(ownerClient, "dashboard-summary-older");
        var newerInterviewId = await StartInterviewAsync(ownerClient, "dashboard-summary-newer");

        using var otherClient = factory.CreateHttpsClient();
        var other = await RegisterAsync(otherClient);
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        var otherInterviewId = await StartInterviewAsync(otherClient, "dashboard-summary-other");

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var now = DateTimeOffset.UtcNow;
            var older = await db.InterviewSessions.SingleAsync(item => item.Id == olderInterviewId);
            var newer = await db.InterviewSessions.SingleAsync(item => item.Id == newerInterviewId);
            older.UpdatedAt = now.AddMinutes(-2);
            newer.UpdatedAt = now;
            db.InterviewReports.Add(new InterviewReport
            {
                Id = Guid.NewGuid(), UserId = owner.UserId, InterviewSessionId = newerInterviewId, OverallScore = 88,
                Rubric = "[]", Strengths = "[]", Gaps = "[]", ActionPlan = "[]", Disclaimer = "test",
                ModelVersion = "test", PromptVersion = "test", RubricVersion = "test", SchemaVersion = "test", CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using var response = await ownerClient.GetAsync("/api/v1/dashboard");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var data = await DataAsync(response);
        Assert.Equal(
            ["billing", "interviews", "reports"],
            data.EnumerateObject().Select(item => item.Name).ToArray());
        Assert.Equal(JsonValueKind.Object, data.GetProperty("billing").ValueKind);

        var billingEntitlement = data.GetProperty("billing").GetProperty("entitlement");
        Assert.Equal("practice", billingEntitlement.GetProperty("planCode").GetString()![..8]);
        Assert.Contains(
            billingEntitlement.GetProperty("features").EnumerateArray(),
            feature => feature.GetProperty("code").GetString() == FeatureValues.InterviewQuestionLimit &&
                feature.GetProperty("limit").GetInt32() == 6);
        var orders = data.GetProperty("billing").GetProperty("orders").EnumerateArray().ToArray();
        Assert.Single(orders);
        Assert.Equal(1, orders[0].GetProperty("amountMinor").GetInt64());

        var interviews = data.GetProperty("interviews").EnumerateArray().ToArray();
        Assert.Equal(2, interviews.Length);
        Assert.Equal(newerInterviewId, interviews[0].GetProperty("id").GetGuid());
        Assert.Equal(olderInterviewId, interviews[1].GetProperty("id").GetGuid());
        Assert.DoesNotContain(interviews, item => item.GetProperty("id").GetGuid() == otherInterviewId);

        var reports = data.GetProperty("reports").EnumerateArray().ToArray();
        Assert.Single(reports);
        Assert.Equal(newerInterviewId, reports[0].GetProperty("interviewId").GetGuid());
        Assert.Equal(88, reports[0].GetProperty("overallScore").GetInt32());
    }

    [Fact]
    public async Task EmptyUserDashboardReturns200WithNullableBillingEntitlement()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var entitlements = await db.Entitlements.Where(item => item.UserId == account.UserId).ToArrayAsync();
            foreach (var entitlement in entitlements)
                entitlement.EndsAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync("/api/v1/dashboard");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var data = await DataAsync(response);
        Assert.Equal(JsonValueKind.Null, data.GetProperty("billing").GetProperty("entitlement").ValueKind);
        Assert.Empty(data.GetProperty("interviews").EnumerateArray());
        Assert.Empty(data.GetProperty("reports").EnumerateArray());
    }

    [Fact]
    public async Task PartialReportUsesAnsweredTranscriptAndPersistedAnswerCoaching()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "partial-report-start");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestion = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstResult = await AnswerAsync(client, interviewId, firstQuestion, "First grounded answer.", "partial-report-answer-one");
        var secondQuestion = firstResult.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var secondResult = await AnswerAsync(client, interviewId, secondQuestion, "Second grounded answer.", "partial-report-answer-two");
        Assert.Equal(InterviewContinuationValues.InProgress, secondResult.GetProperty("continuation").GetProperty("state").GetString());
        Assert.True(secondResult.GetProperty("continuation").GetProperty("canFinishNow").GetBoolean());
        Assert.False(secondResult.GetProperty("continuation").GetProperty("canUpgradeAndContinue").GetBoolean());

        await CompleteAsync(client, interviewId, "partial-report-complete");
        await ProcessJobsAsync(factory);

        using var reportResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        var report = await DataAsync(reportResponse);
        var sample = report.GetProperty("sample");
        Assert.Equal(2, sample.GetProperty("answeredQuestions").GetInt32());
        Assert.Equal(3, sample.GetProperty("issuedQuestions").GetInt32());
        Assert.True(sample.GetProperty("isPartial").GetBoolean());
        Assert.Contains("partial sample", report.GetProperty("disclaimer").GetString(), StringComparison.OrdinalIgnoreCase);

        var reviews = report.GetProperty("questionReviews").EnumerateArray().ToArray();
        Assert.Equal(2, reviews.Length);
        Assert.All(reviews, review =>
        {
            Assert.Equal(JsonValueKind.Null, review.GetProperty("parentQuestionId").ValueKind);
            Assert.NotEmpty(review.GetProperty("rubric").EnumerateArray());
            Assert.NotEmpty(review.GetProperty("strengths").EnumerateArray());
            Assert.NotEmpty(review.GetProperty("improvements").EnumerateArray());
            Assert.False(string.IsNullOrWhiteSpace(review.GetProperty("suggestedImprovedAnswer").GetString()));
        });
        Assert.Equal(2, report.GetProperty("suggestedImprovedAnswers").GetArrayLength());

        Assert.Equal(1, aiProvider.GetCallCount(AiPurposes.InterviewReport));
        var reportInvocation = aiProvider.Invocations.Single(item => item.Purpose == AiPurposes.InterviewReport);
        Assert.DoesNotContain("A: \n", reportInvocation.UntrustedInput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DuplicateReportDeliveryLeavesOneReportAndOneCompletionEvent()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "duplicate-report-start");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var first = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstResult = await AnswerAsync(client, interviewId, first, "First grounded answer.", "duplicate-report-answer-one");
        var second = firstResult.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var secondResult = await AnswerAsync(client, interviewId, second, "Second grounded answer.", "duplicate-report-answer-two");
        var third = secondResult.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, third, "Third grounded answer.", "duplicate-report-answer-three");
        await CompleteAsync(client, interviewId, "duplicate-report-complete");
        await ProcessJobsAsync(factory);

        var reportCalls = aiProvider.GetCallCount(AiPurposes.InterviewReport);
        var duplicateJobId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.OutboxEvents.Add(new OutboxEvent
            {
                Id = duplicateJobId,
                Type = "InterviewReportRequested",
                AggregateType = "interview",
                AggregateId = interviewId,
                Payload = JsonSerializer.Serialize(new { aggregateId = interviewId }, JsonOptions),
                Status = BillingValues.Pending,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        await ProcessJobsAsync(factory);

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(reportCalls, aiProvider.GetCallCount(AiPurposes.InterviewReport));
        Assert.Equal(1, await finalDb.InterviewReports.CountAsync(item => item.InterviewSessionId == interviewId));
        Assert.Equal(1, await finalDb.RealtimeNotifications.CountAsync(item => item.ResourceId == interviewId && item.Status == PracticeValues.Completed));
        Assert.Equal(BillingValues.Processed, (await finalDb.OutboxEvents.SingleAsync(item => item.Id == duplicateJobId)).Status);
    }

    [Fact]
    public async Task ReportStarSummaryMergesPrimaryAndFollowUpEvidenceAtStoryLevel()
    {
        var report = await RunScriptedStarInterviewV2Async(
            Star(90, 85, 70, 95),
            Star(20, 10, 96, 30),
            "star-story-strongest-evidence");
        var sample = report.GetProperty("sample");
        Assert.Equal(4, sample.GetProperty("answeredQuestions").GetInt32());
        Assert.Equal(4, sample.GetProperty("issuedQuestions").GetInt32());
        Assert.False(sample.GetProperty("isPartial").GetBoolean());
        Assert.Equal(4, report.GetProperty("questionReviews").GetArrayLength());
        var summary = report.GetProperty("starSummary");
        var averages = summary.GetProperty("componentAverages");

        Assert.Equal(2, summary.GetProperty("applicableAnswers").GetInt32());
        Assert.Equal(90, averages.GetProperty("situation").GetInt32());
        Assert.Equal(85, averages.GetProperty("task").GetInt32());
        Assert.Equal(96, averages.GetProperty("action").GetInt32());
        Assert.Equal(95, averages.GetProperty("result").GetInt32());
        Assert.Equal(92, summary.GetProperty("averageScore").GetInt32());
        Assert.Equal("action", summary.GetProperty("strongestComponent").GetString());
        Assert.Equal("task", summary.GetProperty("weakestComponent").GetString());
        Assert.Empty(summary.GetProperty("recurringIssues").EnumerateArray());
    }

    [Fact]
    public async Task ReportStarSummaryAllowsFollowUpToResolveMissingComponent()
    {
        var report = await RunScriptedStarInterviewV2Async(
            Star(85, 80, 90, 0, resultDetected: false),
            Star(20, 10, 30, 88),
            "star-story-resolved-result");
        var summary = report.GetProperty("starSummary");
        var averages = summary.GetProperty("componentAverages");

        Assert.Equal(88, averages.GetProperty("result").GetInt32());
        Assert.Equal(86, summary.GetProperty("averageScore").GetInt32());
        Assert.DoesNotContain("result", summary.GetProperty("recurringIssues").EnumerateArray()
            .Select(item => item.GetString()));
    }

    [Fact]
    public async Task ReportStarSummaryKeepsUnresolvedComponentMissing()
    {
        var report = await RunScriptedStarInterviewV2Async(
            Star(85, 80, 90, 0, resultDetected: false),
            Star(20, 10, 30, 40),
            "star-story-unresolved-result");
        var summary = report.GetProperty("starSummary");

        Assert.Equal(40, summary.GetProperty("componentAverages").GetProperty("result").GetInt32());
        Assert.Contains("result", summary.GetProperty("recurringIssues").EnumerateArray()
            .Select(item => item.GetString()));
    }

    [Fact]
    public async Task PrimaryQuestionLineageAndStarSummaryDoNotUseSequenceAsFollowUp()
    {
        var aiProvider = new TestAiProvider();
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithoutStar());
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithStar(Star(60, 60, 60, 60)));
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithoutStar());
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "explicit-question-lineage");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestion = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestion, "Câu trả lời chính cho câu hỏi đầu tiên.", "explicit-lineage-one");
        var followupQuestion = firstAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();

        var primaryQuestion = new InterviewQuestion
        {
            Id = Guid.NewGuid(),
            InterviewSessionId = interviewId,
            Sequence = 3,
            Kind = InterviewQuestionValues.Primary,
            Topic = InterviewQuestionValues.MotivationRoleFit,
            Content = "Vì sao bạn phù hợp với vai trò này?",
            PromptVersion = "test-prompt",
            ModelVersion = "test-model",
            CreatedAt = DateTimeOffset.UtcNow
        };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            db.InterviewQuestions.Add(primaryQuestion);
            await db.SaveChangesAsync();
        }

        var followupResult = await AnswerAsync(client, interviewId, followupQuestion, "Bổ sung chi tiết cho cùng câu chuyện.", "explicit-lineage-two");
        Assert.False(followupResult.GetProperty("isComplete").GetBoolean());

        var afterFollowup = await GetInterviewAsync(client, interviewId);
        var primaryQuestionView = afterFollowup.GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == primaryQuestion.Id);
        Assert.Equal(InterviewQuestionValues.Primary, primaryQuestionView.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.MotivationRoleFit, primaryQuestionView.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, primaryQuestionView.GetProperty("parentQuestionId").ValueKind);

        await AnswerAsync(client, interviewId, primaryQuestion.Id, "Tôi phù hợp với vai trò nhờ kinh nghiệm liên quan.", "explicit-lineage-three");
        await CompleteAsync(client, interviewId, "explicit-lineage-complete");
        await ProcessJobsAsync(factory);

        using var reportResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report");
        var report = await DataAsync(reportResponse);
        var summary = report.GetProperty("starSummary");
        Assert.Equal(1, summary.GetProperty("applicableAnswers").GetInt32());
        Assert.Equal(60, summary.GetProperty("averageScore").GetInt32());
        Assert.Equal(60, summary.GetProperty("componentAverages").GetProperty("action").GetInt32());
        Assert.Contains(aiProvider.Invocations, invocation =>
            invocation.Purpose == AiPurposes.InterviewEvaluate &&
            invocation.UntrustedInput.Contains("is-follow-up: false", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidFollowUpParentRelationshipIsRejected()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 2);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var firstInterviewId = await StartInterviewAsync(client, "invalid-parent-first");
        var secondInterviewId = await StartInterviewAsync(client, "invalid-parent-second");
        await ProcessJobsAsync(factory);

        Guid secondQuestionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var secondQuestion = await db.InterviewQuestions
                .Where(item => item.InterviewSessionId == secondInterviewId)
                .SingleAsync();
            secondQuestionId = secondQuestion.Id;
            db.InterviewQuestions.Add(new InterviewQuestion
            {
                Id = Guid.NewGuid(),
                InterviewSessionId = firstInterviewId,
                Sequence = 2,
                Kind = InterviewQuestionValues.Followup,
                Topic = secondQuestion.Topic,
                ParentQuestionId = secondQuestionId,
                Content = "Invalid cross-session follow-up",
                PromptVersion = "test-prompt",
                ModelVersion = "test-model",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        using var response = await client.GetAsync($"/api/v1/interviews/{firstInterviewId}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_INTERVIEW_STATE", document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task StarSummaryTieBreakUsesPrimaryRootSequence()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "star-root-tie-break");
        await ProcessJobsAsync(factory);

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var firstQuestion = await db.InterviewQuestions.SingleAsync(item => item.InterviewSessionId == interviewId);
            var secondQuestion = new InterviewQuestion
            {
                Id = Guid.NewGuid(),
                InterviewSessionId = interviewId,
                Sequence = 2,
                Kind = InterviewQuestionValues.Primary,
                Topic = InterviewQuestionValues.BehavioralStar,
                Content = "Tell another independent story.",
                PromptVersion = "test-prompt",
                ModelVersion = "test-model",
                CreatedAt = DateTimeOffset.UtcNow
            };
            var followupQuestion = new InterviewQuestion
            {
                Id = Guid.NewGuid(),
                InterviewSessionId = interviewId,
                Sequence = 3,
                Kind = InterviewQuestionValues.Followup,
                Topic = firstQuestion.Topic,
                ParentQuestionId = firstQuestion.Id,
                Content = "Add the result for the first story.",
                PromptVersion = "test-prompt",
                ModelVersion = "test-model",
                CreatedAt = DateTimeOffset.UtcNow
            };
            var now = DateTimeOffset.UtcNow;
            db.AddRange(
                secondQuestion,
                followupQuestion,
                new InterviewAnswer
                {
                    Id = Guid.NewGuid(),
                    UserId = account.UserId,
                    InterviewSessionId = interviewId,
                    QuestionId = firstQuestion.Id,
                    Content = "Primary answer without STAR.",
                    Evaluation = JsonSerializer.Serialize(AnswerEvaluationWithoutStar(), jsonOptions),
                    CreatedAt = now
                },
                new InterviewAnswer
                {
                    Id = Guid.NewGuid(),
                    UserId = account.UserId,
                    InterviewSessionId = interviewId,
                    QuestionId = secondQuestion.Id,
                    Content = "Independent story.",
                    Evaluation = JsonSerializer.Serialize(AnswerEvaluationWithStar(Star(75, 75, 75, 75)), jsonOptions),
                    CreatedAt = now.AddSeconds(1)
                },
                new InterviewAnswer
                {
                    Id = Guid.NewGuid(),
                    UserId = account.UserId,
                    InterviewSessionId = interviewId,
                    QuestionId = followupQuestion.Id,
                    Content = "Follow-up for the first story.",
                    Evaluation = JsonSerializer.Serialize(AnswerEvaluationWithStar(Star(85, 85, 85, 85)), jsonOptions),
                    CreatedAt = now.AddSeconds(2)
                });
            await db.SaveChangesAsync();
        }

        await CompleteAsync(client, interviewId, "star-root-tie-break-complete");
        await ProcessJobsAsync(factory);

        using var reportResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report");
        var report = await DataAsync(reportResponse);
        var summary = report.GetProperty("starSummary");
        Assert.Equal(1, summary.GetProperty("applicableAnswers").GetInt32());
        Assert.Equal(85, summary.GetProperty("averageScore").GetInt32());
        Assert.Equal(85, summary.GetProperty("componentAverages").GetProperty("action").GetInt32());
    }

    [Fact]
    public async Task TechnicalInterviewReportIncludesOnlyCanonicalStarQuestion()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "star-story-technical", "technical");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestion = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestion, "Dependency injection passes dependencies from outside the class.", "star-story-technical-one");
        var secondQuestion = firstAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var secondAnswer = await AnswerAsync(client, interviewId, secondQuestion, "The composition root owns dependency wiring.", "star-story-technical-two");
        var thirdQuestion = secondAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, thirdQuestion, "The role aligns with my engineering experience.", "star-story-technical-three");
        await CompleteAsync(client, interviewId, "star-story-technical-complete");
        await ProcessJobsAsync(factory);

        using var reportResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        var report = await DataAsync(reportResponse);

        Assert.Equal(1, report.GetProperty("starSummary").GetProperty("applicableAnswers").GetInt32());
    }

    [Fact]
    public async Task TechnicalAnswerKeepsStarInapplicable()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "technical-start", "technical");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestion = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var answer = await AnswerAsync(client, interviewId, firstQuestion, "Dependency injection passes dependencies from outside instead of constructing them inside the class.", "technical-answer");
        var star = answer.GetProperty("answer").GetProperty("evaluation").GetProperty("star");
        Assert.False(star.GetProperty("applicable").GetBoolean());
        Assert.False(star.TryGetProperty("situation", out var situation) && situation.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task TerminalReportFailureCreditsOnceAndRetryIsFreeT07()
    {
        var ai = new ToggleReportAiProvider();
        using var factory = new NexoraApiFactory(ai);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "report-failure-start");
        await ProcessJobsAsync(factory);
        var active = await GetInterviewAsync(client, interviewId);
        var first = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstResult = await AnswerAsync(client, interviewId, first, "Tôi đã xử lý vấn đề và đo kết quả.", "report-answer-one");
        var second = firstResult.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, second, "Tôi sẽ kiểm tra tiến độ sớm hơn.", "report-answer-two");
        var afterSecondAnswer = await GetInterviewAsync(client, interviewId);
        var thirdQuestion = afterSecondAnswer.GetProperty("questions").EnumerateArray().Last().GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, thirdQuestion, "Third primary answer.", "report-answer-three");
        await CompleteAsync(client, interviewId, "report-complete-one");
        using (var processingResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report"))
        {
            var processingBody = await processingResponse.Content.ReadAsStringAsync();
            Assert.True(processingResponse.StatusCode == HttpStatusCode.Conflict, processingBody);
            using var processingDocument = JsonDocument.Parse(processingBody);
            Assert.Equal("INTERVIEW_REPORT_PROCESSING", processingDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        await ProcessJobsAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(PracticeValues.Completing, (await db.InterviewSessions.SingleAsync(item => item.Id == interviewId)).Status);
            Assert.Equal(1, (await db.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free")).Adjustment);
            Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.Action == BillingValues.Adjustment && item.SourceType == "report_failure"));
            // Report failures deliberately keep completing and do not publish a misleading interview.failed event.
            Assert.Equal(1, await db.RealtimeNotifications.CountAsync(item => item.ResourceId == interviewId));
            Assert.Equal("active", (await db.RealtimeNotifications.SingleAsync(item => item.ResourceId == interviewId)).Status);
        }

        using (var failedResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report"))
        {
            var failedBody = await failedResponse.Content.ReadAsStringAsync();
            Assert.True(failedResponse.StatusCode == HttpStatusCode.Conflict, failedBody);
            using var failedDocument = JsonDocument.Parse(failedBody);
            Assert.Equal("INTERVIEW_REPORT_FAILED", failedDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        await RetryReportAsync(client, interviewId, "report-retry-one");
        await RetryReportAsync(client, interviewId, "report-retry-one");
        ai.FailReport = false;
        await ProcessJobsAsync(factory);
        var completed = await GetInterviewAsync(client, interviewId);
        Assert.Equal(PracticeValues.Completed, completed.GetProperty("status").GetString());
        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, (await finalDb.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free")).Consumed);
        Assert.Equal(1, (await finalDb.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free")).Adjustment);
        Assert.Equal(1, await finalDb.InterviewReports.CountAsync(item => item.InterviewSessionId == interviewId));
    }

    [Fact]
    public async Task ReportSemanticInvalidTwiceDoesNotPersistFabricatedReport()
    {
        var aiProvider = new TestAiProvider();
        var invalid = new InterviewReportOutput(
            [
                new RubricScore("correctness", 75, "Grounded feedback."),
                new RubricScore("structure", 70, "Grounded feedback."),
                new RubricScore("completeness", 65, "Grounded feedback."),
                new RubricScore("clarity", 80, "Grounded feedback.")
            ],
            [],
            ["Grounded gap"],
            ["Grounded action"],
            AiOperations.ScoreScale);
        aiProvider.EnqueueResponse(AiPurposes.InterviewReport, invalid);
        aiProvider.EnqueueResponse(AiPurposes.InterviewReport, invalid);
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "invalid-report-start");
        await ProcessJobsAsync(factory);
        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestionId = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestionId, "Tôi phân tích nguyên nhân và xử lý sự cố.", "invalid-report-answer-one");
        var secondQuestionId = firstAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, secondQuestionId, "Kết quả là hệ thống ổn định hơn.", "invalid-report-answer-two");
        var afterSecondAnswer = await GetInterviewAsync(client, interviewId);
        var thirdQuestion = afterSecondAnswer.GetProperty("questions").EnumerateArray().Last().GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, thirdQuestion, "Third primary answer.", "invalid-report-answer-three");
        await CompleteAsync(client, interviewId, "invalid-report-complete");

        await ProcessJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewReport));
        Assert.Equal(PracticeValues.Completing, (await db.InterviewSessions.SingleAsync(item => item.Id == interviewId)).Status);
        Assert.Equal(0, await db.InterviewReports.CountAsync(item => item.InterviewSessionId == interviewId));
    }

    private static async Task<Guid> StartInterviewAsync(HttpClient client, string key, string interviewType = "behavioral")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new { role = "Business Analyst", seniority = "junior", interviewType, difficulty = "medium" })
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task RetryReportAsync(HttpClient client, Guid interviewId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/report/retry");
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<JsonElement> AnswerAsync(HttpClient client, Guid interviewId, Guid questionId, string content, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new { questionId, content, durationSeconds = 45 })
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await DataAsync(response);
    }

    private static async Task<HttpResponseMessage> ContinueAsync(HttpClient client, Guid interviewId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<JsonElement> GetInterviewAsync(HttpClient client, Guid interviewId)
    {
        using var response = await client.GetAsync($"/api/v1/interviews/{interviewId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await DataAsync(response);
    }

    private static async Task CompleteAsync(HttpClient client, Guid interviewId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/complete");
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task ProcessJobsAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>().ProcessPendingAsync(CancellationToken.None);
    }

    private static async Task<(Guid PriceId, string PlanCode)> SeedPaidContinuationPlanAsync(NexoraApiFactory factory, int questionLimit)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan
        {
            Id = Guid.NewGuid(),
            Code = $"a7-paid-{Guid.NewGuid():N}",
            Name = "A7 paid continuation test plan",
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
            InterviewQuota = 5,
            IsActive = true,
            CreatedAt = now
        };
        var definition = await db.FeatureDefinitions.SingleAsync(item => item.Code == FeatureValues.InterviewQuestionLimit);
        price.Features.Add(new PlanPriceFeature
        {
            Id = Guid.NewGuid(),
            PlanPriceId = price.Id,
            FeatureDefinitionId = definition.Id,
            IsEnabled = true,
            Limit = questionLimit,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.AddRange(plan, price);
        await db.SaveChangesAsync();
        return (price.Id, plan.Code);
    }

    private static async Task<Guid> CreateCheckoutAsync(HttpClient client, Guid planPriceId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions")
        {
            Content = JsonContent.Create(new { planPriceId })
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("orderId").GetGuid();
    }

    private static async Task<Webhook> CreateFakeWebhookAsync(NexoraApiFactory factory, Guid orderId, string eventId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var order = await db.Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId,
            orderId,
            transactionId = order.ProviderTransactionId,
            amountMinor = order.AmountMinor,
            currency = order.Currency,
            status = "paid",
            occurredAt = DateTimeOffset.UtcNow
        }, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TestWebhookKey));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(body)}"))).ToLowerInvariant();
        return new Webhook(body, timestamp, signature);
    }

    private static async Task SeedEntitlementAsync(NexoraApiFactory factory, Guid userId, int quota, int? questionLimit = null)
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
        if (questionLimit is not null)
        {
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
        }
        await db.SaveChangesAsync();
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"practice-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Practice candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
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

    private static async Task<JsonElement> RunScriptedStarInterviewV2Async(
        StarEvaluation primary,
        StarEvaluation followUp,
        string key)
    {
        var aiProvider = new TestAiProvider();
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithoutStar());
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithStar(primary));
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithoutStar());
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithStar(followUp));
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, key);
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var q2 = (await AnswerAsync(client, interviewId, q1, "First primary answer.", $"{key}-one"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3 = (await AnswerAsync(client, interviewId, q2, "STAR primary answer.", $"{key}-two"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3Result = await AnswerAsync(client, interviewId, q3, "Third primary answer.", $"{key}-three");
        Assert.Equal(JsonValueKind.Null, q3Result.GetProperty("nextQuestion").ValueKind);

        Guid q4;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var starPrimary = await db.InterviewQuestions.SingleAsync(item =>
                item.InterviewSessionId == interviewId && item.Topic == InterviewQuestionValues.BehavioralStar);
            var followup = new InterviewQuestion
            {
                Id = Guid.NewGuid(),
                InterviewSessionId = interviewId,
                Sequence = 4,
                Kind = InterviewQuestionValues.Followup,
                Topic = InterviewQuestionValues.BehavioralStar,
                ParentQuestionId = starPrimary.Id,
                Content = "Explicit STAR follow-up.",
                PromptVersion = "test-prompt",
                ModelVersion = "test-model",
                CreatedAt = DateTimeOffset.UtcNow
            };
            q4 = followup.Id;
            db.InterviewQuestions.Add(followup);
            await db.SaveChangesAsync();
        }
        await AnswerAsync(client, interviewId, q4, "Follow-up answer.", $"{key}-four");
        await CompleteAsync(client, interviewId, $"{key}-complete");
        await ProcessJobsAsync(factory);

        using var reportResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        return await DataAsync(reportResponse);
    }

    private static async Task<JsonElement> RunScriptedStarInterviewAsync(
        StarEvaluation primary,
        StarEvaluation followUp,
        string key)
    {
        var aiProvider = new TestAiProvider();
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithoutStar());
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithStar(primary));
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithoutStar());
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithStar(followUp));
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, key);
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestion = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestion, "Tôi xử lý câu chuyện theo hướng có bằng chứng.", $"{key}-one");
        var secondQuestion = firstAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, secondQuestion, "Tôi bổ sung chi tiết cho cùng câu chuyện.", $"{key}-two");
        await CompleteAsync(client, interviewId, $"{key}-complete");
        await ProcessJobsAsync(factory);

        using var reportResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report");
        Assert.Equal(HttpStatusCode.OK, reportResponse.StatusCode);
        return await DataAsync(reportResponse);
    }

    private static AnswerEvaluation AnswerEvaluationWithStar(StarEvaluation star) => new(
        [
            new RubricScore("correctness", 80, "Grounded correctness evidence."),
            new RubricScore("structure", 80, "Grounded structure evidence."),
            new RubricScore("completeness", 80, "Grounded completeness evidence."),
            new RubricScore("clarity", 80, "Grounded clarity evidence.")
        ],
        "Grounded answer feedback.",
        star,
        AiOperations.ScoreScale,
        ["The answer has a clear structure."],
        ["Add one concrete example if available."],
        "Keep the same answer and add concrete evidence if available.");

    private static AnswerEvaluation AnswerEvaluationWithoutStar() => new(
        [
            new RubricScore("correctness", 80, "Grounded correctness evidence."),
            new RubricScore("structure", 80, "Grounded structure evidence."),
            new RubricScore("completeness", 80, "Grounded completeness evidence."),
            new RubricScore("clarity", 80, "Grounded clarity evidence.")
        ],
        "Grounded answer feedback.",
        new StarEvaluation(false, null, null, null, null, null, [], [], [], AiOperations.ScoreScale),
        AiOperations.ScoreScale,
        ["The answer has a clear structure."],
        ["Add one concrete example if available."],
        "Keep the same answer and add concrete evidence if available.");

    private static void EnqueueGroundedEvaluation(TestAiProvider aiProvider, AnswerEvaluation evaluation)
    {
        aiProvider.EnqueueHandler(AiPurposes.InterviewEvaluate, request =>
        {
            var candidateAnswer = request.UntrustedInput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("answer:", StringComparison.OrdinalIgnoreCase))
                .Select(line => line["answer:".Length..].Trim())
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            if (string.IsNullOrWhiteSpace(candidateAnswer))
                return evaluation;

            return evaluation with
            {
                Strengths = [$"Grounded answer: {candidateAnswer[..Math.Min(candidateAnswer.Length, 120)]}"],
                ImprovedAnswer = candidateAnswer
            };
        });
    }

    private static StarEvaluation Star(
        int situationScore,
        int taskScore,
        int actionScore,
        int resultScore,
        bool resultDetected = true) => new(
        true,
        null,
        new StarComponentEvaluation(situationScore, true, "Situation evidence.", "Situation feedback."),
        new StarComponentEvaluation(taskScore, true, "Task evidence.", "Task feedback."),
        new StarComponentEvaluation(actionScore, true, "Action evidence.", "Action feedback."),
        resultDetected
            ? new StarComponentEvaluation(resultScore, true, "Result evidence.", "Result feedback.")
            : new StarComponentEvaluation(0, false, string.Empty, "Result feedback."),
        [],
        [],
        [],
        AiOperations.ScoreScale);

    private sealed record Account(Guid UserId, string AccessToken);
    private sealed record Webhook(byte[] Body, string Timestamp, string Signature);

    private sealed class FailingAiProvider : IAiProvider
    {
        public string ModelVersion => "test-gemini-model";

        public Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken) =>
            Task.FromException<T>(new TimeoutException("Deterministic T-07 timeout"));
    }

    private sealed class ToggleReportAiProvider : IAiProvider
    {
        private readonly TestAiProvider _inner = new();
        public bool FailReport { get; set; } = true;
        public string ModelVersion => _inner.ModelVersion;

        public Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken) =>
            FailReport && request.Purpose == "interview.report"
                ? Task.FromException<T>(new TimeoutException("Deterministic terminal report timeout"))
                : _inner.GenerateStructuredAsync<T>(request, cancellationToken);
    }
}
