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
        var freePaywallView = await GetInterviewAsync(client, interviewId);
        Assert.Equal(InterviewQuestionPreparationStates.Ready,
            freePaywallView.GetProperty("questionPreparationState").GetString());

        using var retryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/questions/retry");
        retryRequest.Headers.Add("Idempotency-Key", "a7-free-question-retry");
        using var retryResponse = await client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.Forbidden, retryResponse.StatusCode);
        Assert.Contains("INTERVIEW_UPGRADE_REQUIRED", await retryResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);
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
        await ProcessJobsAsync(factory);

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
    public async Task ActiveInterviewStopsUsingDeletedResumeForFutureAiCallsAndPracticeAgain()
    {
        const string resumeMarker = "SECRET_DELETED_RESUME_MARKER";
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var resumeId = await SeedReadyResumeWithProfileAsync(factory, account.UserId, aiProvider, resumeMarker);

        using var start = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new
            {
                role = "Backend Engineer",
                seniority = "senior",
                interviewType = "technical",
                difficulty = "medium",
                resumeId
            })
        };
        start.Headers.Add("Idempotency-Key", "deleted-resume-active-start");
        JsonElement started;
        using (var startResponse = await client.SendAsync(start))
        {
            Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
            started = await DataAsync(startResponse);
        }
        var interviewId = started.GetProperty("id").GetGuid();
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        Assert.Equal(PracticeValues.Active, active.GetProperty("status").GetString());
        var firstQuestion = active.GetProperty("questions")[0];
        var firstQuestionId = firstQuestion.GetProperty("id").GetGuid();
        var firstQuestionContent = firstQuestion.GetProperty("content").GetString();
        Assert.All(aiProvider.Invocations.Where(item => item.Purpose == AiPurposes.InterviewFirstQuestion),
            item => Assert.Contains(resumeMarker, item.UntrustedInput, StringComparison.Ordinal));

        var firstAnswerContent = "Tôi đã cải thiện độ tin cậy của dịch vụ bằng kiểm thử và giám sát.";
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestionId, firstAnswerContent, "deleted-resume-active-answer-one");
        var firstAnswerId = firstAnswer.GetProperty("answer").GetProperty("id").GetGuid();
        var secondQuestion = firstAnswer.GetProperty("nextQuestion");
        var secondQuestionId = secondQuestion.GetProperty("id").GetGuid();
        var secondQuestionContent = secondQuestion.GetProperty("content").GetString();
        await ProcessJobsAsync(factory);
        Assert.Contains(resumeMarker, aiProvider.Invocations
            .Last(item => item.Purpose == AiPurposes.InterviewEvaluate).UntrustedInput, StringComparison.Ordinal);

        string firstEvaluation;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            firstEvaluation = (await db.InterviewAnswers.Where(item => item.Id == firstAnswerId)
                .Select(item => item.Evaluation).SingleAsync())!;
        }

        var invocationCountBeforeDelete = aiProvider.Invocations.Count;
        using (var delete = await client.DeleteAsync($"/api/v1/resumes/{resumeId}"))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var deletedAnswer = await AnswerAsync(
            client, interviewId, secondQuestionId,
            "Tôi xử lý truy vấn chậm và xác nhận độ trễ giảm sau khi triển khai.",
            "deleted-resume-active-answer-two");
        var thirdQuestion = deletedAnswer.GetProperty("nextQuestion");
        var thirdQuestionId = thirdQuestion.GetProperty("id").GetGuid();
        Assert.Equal(InterviewQuestionValues.CvTargeted, thirdQuestion.GetProperty("topic").GetString());
        await ProcessJobsAsync(factory);
        var afterDeleteAnswerCalls = aiProvider.Invocations.Skip(invocationCountBeforeDelete).ToArray();
        Assert.Single(afterDeleteAnswerCalls, item => item.Purpose == AiPurposes.InterviewEvaluate);
        Assert.All(afterDeleteAnswerCalls, item =>
            Assert.DoesNotContain(resumeMarker, item.UntrustedInput, StringComparison.Ordinal));

        var thirdAnswer = await AnswerAsync(client, interviewId, thirdQuestionId,
            "Tôi theo dõi chỉ số sau phát hành và chia sẻ kết quả với nhóm.",
            "deleted-resume-active-answer-three");
        var fourthQuestion = thirdAnswer.GetProperty("nextQuestion");
        Assert.Equal(InterviewQuestionValues.CvTargeted, fourthQuestion.GetProperty("topic").GetString());

        await AnswerAsync(client, interviewId, fourthQuestion.GetProperty("id").GetGuid(),
            "Tôi tổng kết kết quả bằng số liệu đã theo dõi.", "deleted-resume-active-answer-four");
        await CompleteAsync(client, interviewId, "deleted-resume-active-complete");
        await ProcessJobsAsync(factory);

        Assert.All(aiProvider.Invocations.Skip(invocationCountBeforeDelete), item =>
            Assert.DoesNotContain(resumeMarker, item.UntrustedInput, StringComparison.Ordinal));

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var session = await db.InterviewSessions.SingleAsync(item => item.Id == interviewId);
            Assert.Equal(PracticeValues.Completed, session.Status);
            Assert.Equal(resumeId, session.ResumeId);
            Assert.NotNull(await db.Resumes.Where(item => item.Id == resumeId).Select(item => item.DeletedAt).SingleAsync());
            Assert.Equal(firstQuestionContent, await db.InterviewQuestions.Where(item => item.Id == firstQuestionId)
                .Select(item => item.Content).SingleAsync());
            Assert.Equal(secondQuestionContent, await db.InterviewQuestions.Where(item => item.Id == secondQuestionId)
                .Select(item => item.Content).SingleAsync());
            var persistedAnswer = await db.InterviewAnswers.SingleAsync(item => item.Id == firstAnswerId);
            Assert.Equal(firstAnswerContent, persistedAnswer.Content);
            Assert.Equal(firstEvaluation, persistedAnswer.Evaluation);
            Assert.True(await db.InterviewReports.AnyAsync(item => item.InterviewSessionId == interviewId));
        }

        var callCountBeforePracticeAgain = aiProvider.TotalCalls;
        using var practiceAgain = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/practice-again")
        {
            Content = JsonContent.Create(new { focus = (string?)null, reason = (string?)null })
        };
        practiceAgain.Headers.Add("Idempotency-Key", "deleted-resume-practice-again");
        using var practiceAgainResponse = await client.SendAsync(practiceAgain);
        Assert.Equal(HttpStatusCode.NotFound, practiceAgainResponse.StatusCode);
        Assert.Equal(callCountBeforePracticeAgain, aiProvider.TotalCalls);
    }

    [Fact]
    public async Task DeletedResumeIsOmittedFromFutureAnswerEvaluationContext()
    {
        const string resumeMarker = "SECRET_DELETED_RESUME_MARKER";
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var resumeId = await SeedReadyResumeWithProfileAsync(factory, account.UserId, aiProvider, resumeMarker);

        using var start = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new
            {
                role = "Backend Engineer",
                seniority = "senior",
                interviewType = "behavioral",
                difficulty = "medium",
                resumeId
            })
        };
        start.Headers.Add("Idempotency-Key", "deleted-resume-followup-start");
        JsonElement started;
        using (var response = await client.SendAsync(start))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            started = await DataAsync(response);
        }
        var interviewId = started.GetProperty("id").GetGuid();
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestionId = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var secondQuestion = await AnswerAsync(
            client, interviewId, firstQuestionId, "Tôi điều phối nhóm qua một sự cố khó.", "deleted-resume-followup-answer-one");
        var secondQuestionId = secondQuestion.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        Assert.Equal(InterviewQuestionValues.BehavioralStar,
            secondQuestion.GetProperty("nextQuestion").GetProperty("topic").GetString());

        var invocationCountBeforeDelete = aiProvider.Invocations.Count;
        using (var delete = await client.DeleteAsync($"/api/v1/resumes/{resumeId}"))
            Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        var thirdQuestion = await AnswerAsync(
            client, interviewId, secondQuestionId, "Tôi đã phối hợp khắc phục và kiểm tra lại.", "deleted-resume-followup-answer-two");
        var thirdQuestionId = thirdQuestion.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var thirdAnswer = await AnswerAsync(client, interviewId, thirdQuestionId,
            "Nhóm thống nhất được phương án và cải thiện bàn giao.", "deleted-resume-followup-answer-three");
        var fourthQuestion = thirdAnswer.GetProperty("nextQuestion");

        var fourthAnswer = await AnswerAsync(client, interviewId, fourthQuestion.GetProperty("id").GetGuid(),
            "Tôi trình bày tình huống, hành động và kết quả.", "deleted-resume-followup-answer-four");
        Assert.Equal(5, fourthAnswer.GetProperty("nextQuestion").GetProperty("sequence").GetInt32());
        await ProcessJobsAsync(factory);
        Assert.All(aiProvider.Invocations.Skip(invocationCountBeforeDelete), item =>
            Assert.DoesNotContain(resumeMarker, item.UntrustedInput, StringComparison.Ordinal));
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
        var q4 = a3.GetProperty("nextQuestion");
        Assert.Equal(4, q4.GetProperty("sequence").GetInt32());
        var firstQuestionCallsBeforeContinuation = aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion);

        using var firstRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        firstRequest.Headers.Add("Idempotency-Key", "a7-paid-continue");
        using var firstResponse = await client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var first = await DataAsync(firstResponse);
        Assert.Equal(interviewId, first.GetProperty("id").GetGuid());
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
        Assert.Equal(0, aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion) - firstQuestionCallsBeforeContinuation);

        var q4Answer = await AnswerAsync(client, interviewId, q4.GetProperty("id").GetGuid(), "A measurable technical result.", "a7-paid-a4");
        Assert.False(q4Answer.GetProperty("isComplete").GetBoolean());
        Assert.Equal(InterviewContinuationValues.InProgress, q4Answer.GetProperty("continuation").GetProperty("state").GetString());
        Assert.True(q4Answer.GetProperty("continuation").GetProperty("canFinishNow").GetBoolean());

        var q5 = q4Answer.GetProperty("nextQuestion");
        Assert.Equal(InterviewQuestionValues.Primary, q5.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.Scenario, q5.GetProperty("topic").GetString());
        Assert.Contains("[scenario]", q5.GetProperty("content").GetString(), StringComparison.Ordinal);

        var q5Answer = await AnswerAsync(client, interviewId, q5.GetProperty("id").GetGuid(), "Another measurable technical result.", "a7-paid-a5");
        Assert.True(q5Answer.GetProperty("isComplete").GetBoolean());
        Assert.Equal(InterviewContinuationValues.MaxQuestionsReached, q5Answer.GetProperty("continuation").GetProperty("state").GetString());
        Assert.True(q5Answer.GetProperty("continuation").GetProperty("canFinishNow").GetBoolean());
        Assert.Equal(0, aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion) - firstQuestionCallsBeforeContinuation);

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
    public async Task FailedPaidContinuationQuestionPlanCanBeRetriedIdempotently()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "a7-retry-plan-start", "technical");
        await ProcessJobsAsync(factory);
        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var q2 = (await AnswerAsync(client, interviewId, q1, "Primary answer one.", "a7-retry-plan-a1"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3 = (await AnswerAsync(client, interviewId, q2, "Primary answer two.", "a7-retry-plan-a2"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var freeResult = await AnswerAsync(client, interviewId, q3, "Primary answer three.", "a7-retry-plan-a3");
        Assert.Equal(InterviewContinuationValues.UpgradeRequired,
            freeResult.GetProperty("continuation").GetProperty("state").GetString());

        await SeedEntitlementAsync(factory, account.UserId, 1, questionLimit: 6);
        using (var continueRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue"))
        {
            continueRequest.Headers.Add("Idempotency-Key", "a7-retry-plan-continue");
            using var continueResponse = await client.SendAsync(continueRequest);
            Assert.Equal(HttpStatusCode.OK, continueResponse.StatusCode);
            var queued = await DataAsync(continueResponse);
            Assert.Equal(InterviewQuestionPreparationStates.Processing,
                queued.GetProperty("questionPreparationState").GetString());
        }

        aiProvider.EnqueueResponse(AiPurposes.InterviewFirstQuestion,
            new AiProviderException(AiProviderFailureKind.Unavailable, "question plan failure"));
        aiProvider.EnqueueResponse(AiPurposes.InterviewFirstQuestion,
            new AiProviderException(AiProviderFailureKind.Unavailable, "question plan failure retry"));
        await ProcessJobsAsync(factory);

        var failed = await GetInterviewAsync(client, interviewId);
        Assert.Equal(PracticeValues.Active, failed.GetProperty("status").GetString());
        Assert.Equal(InterviewResultStates.Collecting, failed.GetProperty("resultState").GetString());
        Assert.Equal(InterviewQuestionPreparationStates.Failed,
            failed.GetProperty("questionPreparationState").GetString());
        Assert.Equal(3, failed.GetProperty("questions").GetArrayLength());

        using (var retryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/questions/retry"))
        {
            retryRequest.Headers.Add("Idempotency-Key", "a7-retry-plan-retry");
            using var retryResponse = await client.SendAsync(retryRequest);
            Assert.Equal(HttpStatusCode.Accepted, retryResponse.StatusCode);
            var retryView = await DataAsync(retryResponse);
            Assert.Equal(InterviewQuestionPreparationStates.Processing,
                retryView.GetProperty("questionPreparationState").GetString());
        }

        using (var replayRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/questions/retry"))
        {
            replayRequest.Headers.Add("Idempotency-Key", "a7-retry-plan-retry");
            using var replayResponse = await client.SendAsync(replayRequest);
            Assert.Equal(HttpStatusCode.Accepted, replayResponse.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(2, await db.OutboxEvents.CountAsync(item =>
                item.Type == "InterviewQuestionPlanRequested" && item.AggregateId == interviewId));
            Assert.Equal(3, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
        }

        await ProcessJobsAsync(factory);
        var recovered = await GetInterviewAsync(client, interviewId);
        Assert.Equal(InterviewQuestionPreparationStates.Ready,
            recovered.GetProperty("questionPreparationState").GetString());
        var q4 = recovered.GetProperty("questions").EnumerateArray().Single(item => item.GetProperty("sequence").GetInt32() == 4);
        Assert.Equal(4, q4.GetProperty("sequence").GetInt32());
        Assert.Equal(4, recovered.GetProperty("questions").GetArrayLength());

        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var sequences = await finalDb.InterviewQuestions
            .Where(item => item.InterviewSessionId == interviewId)
            .Select(item => item.Sequence)
            .ToArrayAsync();
        Assert.Equal(5, sequences.Length);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());
        Assert.Equal(2, await finalDb.OutboxEvents.CountAsync(item =>
            item.Type == "InterviewQuestionPlanRequested" && item.AggregateId == interviewId));
    }

    [Fact]
    public async Task UnlimitedEntitlementStopsAtFiveQuestions()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1, unlimitedQuestionLimit: true);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "a7-unlimited-plan-start", "technical");
        await ProcessJobsAsync(factory);
        var current = await GetInterviewAsync(client, interviewId);
        Assert.Equal(1, current.GetProperty("questions").GetArrayLength());

        for (var sequence = 1; sequence <= 5; sequence++)
        {
            var question = current.GetProperty("questions").EnumerateArray()
                .Single(item => item.GetProperty("sequence").GetInt32() == sequence);
            var answer = await AnswerAsync(
                client,
                interviewId,
                question.GetProperty("id").GetGuid(),
                $"Unlimited answer {sequence}.",
                $"a7-unlimited-plan-answer-{sequence}");
            if (sequence < 5)
            {
                Assert.Equal(sequence + 1, answer.GetProperty("nextQuestion").GetProperty("sequence").GetInt32());
                current = await GetInterviewAsync(client, interviewId);
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, answer.GetProperty("nextQuestion").ValueKind);
                Assert.Equal(InterviewContinuationValues.MaxQuestionsReached,
                    answer.GetProperty("continuation").GetProperty("state").GetString());
            }
        }

        var callsAtCap = aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion);

        using var retryRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/questions/retry");
        retryRequest.Headers.Add("Idempotency-Key", "a7-unlimited-plan-retry");
        using var retryResponse = await client.SendAsync(retryRequest);
        Assert.Equal(HttpStatusCode.Conflict, retryResponse.StatusCode);
        await ProcessJobsAsync(factory);

        var capped = await GetInterviewAsync(client, interviewId);
        Assert.Equal(5, capped.GetProperty("questions").GetArrayLength());
        Assert.Equal(callsAtCap, aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var sequences = await db.InterviewQuestions
            .Where(item => item.InterviewSessionId == interviewId)
            .Select(item => item.Sequence)
            .ToArrayAsync();
        Assert.Equal(5, sequences.Length);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());
        Assert.Equal(0, await db.OutboxEvents.CountAsync(item =>
            item.Type == "InterviewQuestionPlanRequested" && item.AggregateId == interviewId));
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
        await ProcessJobsAsync(factory);
        var continued = await GetInterviewAsync(client, interviewId);
        Assert.Equal(interviewId, continued.GetProperty("id").GetGuid());
        var q4 = continued.GetProperty("questions").EnumerateArray().Single(item => item.GetProperty("sequence").GetInt32() == 4);
        Assert.Equal(InterviewQuestionValues.Primary, q4.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.Technical, q4.GetProperty("topic").GetString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var session = await db.InterviewSessions.SingleAsync(item => item.Id == interviewId);
        Assert.Equal(5, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
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
        await ProcessJobsAsync(factory);
        var q4 = (await GetInterviewAsync(client, interviewId)).GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("sequence").GetInt32() == 4);
        var q4Answer = await AnswerAsync(client, interviewId, q4.GetProperty("id").GetGuid(), "Paid answer four.", "a7-paid-cap-a4");
        Assert.Equal(InterviewContinuationValues.InProgress, q4Answer.GetProperty("continuation").GetProperty("state").GetString());

        var q5 = q4Answer.GetProperty("nextQuestion");
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
        Assert.Equal(5, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
        Assert.Equal(2, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId && item.Sequence > InterviewQuestionValues.FreeQuestionLimit));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Reserve));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Consume));
    }

    [Fact]
    public async Task PaidBehavioralProgressionUsesDeterministicPreparedPrimaryQuestions()
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
        var thirdAnswer = await AnswerAsync(client, interviewId, q3, "Tôi phù hợp với vai trò.", "a7-paid-behavioral-a3");
        var q4 = thirdAnswer.GetProperty("nextQuestion");
        Assert.Equal(InterviewQuestionValues.Behavioral, q4.GetProperty("topic").GetString());
        Assert.Equal(InterviewQuestionValues.Primary, q4.GetProperty("kind").GetString());
        var q4Id = q4.GetProperty("id").GetGuid();

        var fourthAnswer = await AnswerAsync(client, interviewId, q4Id, "Tôi đã phối hợp với nhóm để xử lý sự cố.", "a7-paid-behavioral-a4");
        var q5 = fourthAnswer.GetProperty("nextQuestion");
        Assert.Equal(InterviewQuestionValues.Primary, q5.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.Scenario, q5.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, q5.GetProperty("parentQuestionId").ValueKind);
        Assert.Equal(0, aiProvider.GetCallCount(AiPurposes.InterviewFollowup));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persisted = await db.InterviewQuestions.SingleAsync(item => item.Id == q5.GetProperty("id").GetGuid());
        Assert.Equal(AiOperations.InterviewFirstQuestion.PromptVersion, persisted.PromptVersion);
        Assert.Equal(aiProvider.ModelVersion, persisted.ModelVersion);
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
        var firstStar = (await ProcessAndGetEvaluationAsync(factory, firstAnswer)).GetProperty("star");
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
        Assert.True((await ProcessAndGetEvaluationAsync(factory, secondAnswer)).GetProperty("star").GetProperty("applicable").GetBoolean());
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
        const string candidateAnswer = "First grounded answer.";
        const string illustrativeText = "Ví dụ giả định: Tôi điều phối một nhóm 8 người triển khai nền tảng Atlas.";
        EnqueueGroundedEvaluation(aiProvider, AnswerEvaluationWithoutStar() with
        {
            SampleAnswer = new SampleInterviewAnswer(
                "self_intro", null, null, null, null, illustrativeText)
        });
        var firstResult = await AnswerAsync(client, interviewId, firstQuestion, candidateAnswer, "partial-report-answer-one");
        var persistedAnswer = firstResult.GetProperty("answer");
        Assert.Equal(candidateAnswer, persistedAnswer.GetProperty("content").GetString());
        var persistedEvaluation = await ProcessAndGetEvaluationAsync(factory, firstResult);
        Assert.Equal(candidateAnswer, persistedEvaluation.GetProperty("improvedAnswer").GetString());
        Assert.Equal(illustrativeText, persistedEvaluation.GetProperty("sampleAnswer").GetProperty("fullAnswer").GetString());
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
        var firstReview = Assert.Single(reviews, review => review.GetProperty("answer").GetString() == candidateAnswer);
        Assert.Equal(illustrativeText, firstReview.GetProperty("sampleAnswer").GetProperty("fullAnswer").GetString());
        Assert.Equal(2, report.GetProperty("suggestedImprovedAnswers").GetArrayLength());

        Assert.Equal(1, aiProvider.GetCallCount(AiPurposes.InterviewReport));
        var reportInvocation = aiProvider.Invocations.Single(item => item.Purpose == AiPurposes.InterviewReport);
        Assert.DoesNotContain("A: \n", reportInvocation.UntrustedInput, StringComparison.Ordinal);
        Assert.Contains($"A: {candidateAnswer}", reportInvocation.UntrustedInput, StringComparison.Ordinal);
        Assert.DoesNotContain(illustrativeText, reportInvocation.UntrustedInput, StringComparison.Ordinal);
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
        Assert.Equal(5, sample.GetProperty("issuedQuestions").GetInt32());
        Assert.True(sample.GetProperty("isPartial").GetBoolean());
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

        Guid primaryQuestionId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var primaryQuestion = await db.InterviewQuestions.SingleAsync(item =>
                item.InterviewSessionId == interviewId && item.Sequence == 3);
            primaryQuestion.Kind = InterviewQuestionValues.Primary;
            primaryQuestion.Topic = InterviewQuestionValues.MotivationRoleFit;
            primaryQuestion.ParentQuestionId = null;
            primaryQuestion.Content = "Vì sao bạn phù hợp với vai trò này?";
            primaryQuestionId = primaryQuestion.Id;
            await db.SaveChangesAsync();
        }

        var followupResult = await AnswerAsync(client, interviewId, followupQuestion, "Bổ sung chi tiết cho cùng câu chuyện.", "explicit-lineage-two");
        Assert.False(followupResult.GetProperty("isComplete").GetBoolean());

        var afterFollowup = await GetInterviewAsync(client, interviewId);
        var primaryQuestionView = afterFollowup.GetProperty("questions").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == primaryQuestionId);
        Assert.Equal(InterviewQuestionValues.Primary, primaryQuestionView.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.MotivationRoleFit, primaryQuestionView.GetProperty("topic").GetString());
        Assert.Equal(JsonValueKind.Null, primaryQuestionView.GetProperty("parentQuestionId").ValueKind);

        await AnswerAsync(client, interviewId, primaryQuestionId, "Tôi phù hợp với vai trò nhờ kinh nghiệm liên quan.", "explicit-lineage-three");
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
                .OrderBy(item => item.Sequence)
                .FirstAsync();
            secondQuestionId = secondQuestion.Id;
            var invalidFollowup = await db.InterviewQuestions.SingleAsync(item =>
                item.InterviewSessionId == firstInterviewId && item.Sequence == 2);
            invalidFollowup.Kind = InterviewQuestionValues.Followup;
            invalidFollowup.Topic = secondQuestion.Topic;
            invalidFollowup.ParentQuestionId = secondQuestionId;
            invalidFollowup.Content = "Invalid cross-session follow-up";
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
            var questions = await db.InterviewQuestions.Where(item => item.InterviewSessionId == interviewId)
                .OrderBy(item => item.Sequence).ToArrayAsync();
            var firstQuestion = questions[0];
            var secondQuestion = questions[1];
            secondQuestion.Kind = InterviewQuestionValues.Primary;
            secondQuestion.Topic = InterviewQuestionValues.BehavioralStar;
            secondQuestion.Content = "Tell another independent story.";
            var followupQuestion = questions[2];
            followupQuestion.Kind = InterviewQuestionValues.Followup;
            followupQuestion.Topic = firstQuestion.Topic;
            followupQuestion.ParentQuestionId = firstQuestion.Id;
            followupQuestion.Content = "Add the result for the first story.";
            var now = DateTimeOffset.UtcNow;
            secondQuestion.ReleasedAt = now;
            followupQuestion.ReleasedAt = now;
            db.AddRange(
                new InterviewAnswer
                {
                    Id = Guid.NewGuid(),
                    UserId = account.UserId,
                    InterviewSessionId = interviewId,
                    QuestionId = firstQuestion.Id,
                    Content = "Primary answer without STAR.",
                    Evaluation = JsonSerializer.Serialize(AnswerEvaluationWithoutStar(), jsonOptions),
                    EvaluationStatus = InterviewAnswerEvaluationStates.Ready,
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
                    EvaluationStatus = InterviewAnswerEvaluationStates.Ready,
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
                    EvaluationStatus = InterviewAnswerEvaluationStates.Ready,
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
    public async Task TechnicalInterviewReportDoesNotIncludeStarQuestions()
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

        Assert.Equal(JsonValueKind.Null, report.GetProperty("starSummary").ValueKind);
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
        var star = (await ProcessAndGetEvaluationAsync(factory, answer)).GetProperty("star");
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
        Assert.Equal(
            InterviewReportStates.None,
            (await GetInterviewAsync(client, interviewId)).GetProperty("reportState").GetString());
        Assert.Equal(
            InterviewResultStates.Processing,
            (await GetInterviewAsync(client, interviewId)).GetProperty("resultState").GetString());
        using (var processingResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report"))
        {
            var processingBody = await processingResponse.Content.ReadAsStringAsync();
            Assert.True(processingResponse.StatusCode == HttpStatusCode.Conflict, processingBody);
            using var processingDocument = JsonDocument.Parse(processingBody);
            Assert.Equal("INTERVIEW_REPORT_PROCESSING", processingDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        await ProcessJobsAsync(factory);

        Assert.Equal(
            InterviewReportStates.Failed,
            (await GetInterviewAsync(client, interviewId)).GetProperty("reportState").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(PracticeValues.Completing, (await db.InterviewSessions.SingleAsync(item => item.Id == interviewId)).Status);
            Assert.Equal(1, (await db.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free")).Adjustment);
            Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.Action == BillingValues.Adjustment && item.SourceType == "report_failure"));
            // Evaluation progress is published, but report failure never emits a misleading interview.failed event.
            Assert.Equal(1, await db.RealtimeNotifications.CountAsync(item =>
                item.ResourceId == interviewId && item.Status == PracticeValues.Active));
            Assert.Equal(3, await db.RealtimeNotifications.CountAsync(item =>
                item.ResourceId == interviewId && item.Status == InterviewAnswerEvaluationStates.Ready));
        }

        using (var failedResponse = await client.GetAsync($"/api/v1/interviews/{interviewId}/report"))
        {
            var failedBody = await failedResponse.Content.ReadAsStringAsync();
            Assert.True(failedResponse.StatusCode == HttpStatusCode.Conflict, failedBody);
            using var failedDocument = JsonDocument.Parse(failedBody);
            Assert.Equal("INTERVIEW_REPORT_FAILED", failedDocument.RootElement.GetProperty("error").GetProperty("code").GetString());
        }

        await RetryReportAsync(client, interviewId, "report-retry-one");
        Assert.Equal(
            InterviewReportStates.Processing,
            (await GetInterviewAsync(client, interviewId)).GetProperty("reportState").GetString());
        await RetryReportAsync(client, interviewId, "report-retry-one");
        ai.FailReport = false;
        await ProcessJobsAsync(factory);
        var completed = await GetInterviewAsync(client, interviewId);
        Assert.Equal(PracticeValues.Completed, completed.GetProperty("status").GetString());
        Assert.Equal(InterviewReportStates.Ready, completed.GetProperty("reportState").GetString());
        using var finalScope = factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, (await finalDb.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free")).Consumed);
        Assert.Equal(1, (await finalDb.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free")).Adjustment);
        Assert.Equal(1, await finalDb.InterviewReports.CountAsync(item => item.InterviewSessionId == interviewId));
    }

    [Fact]
    public async Task ReportSemanticInvalidTwiceUsesValidatedAnswerAggregateFallback()
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
        Assert.Equal(PracticeValues.Completed, (await db.InterviewSessions.SingleAsync(item => item.Id == interviewId)).Status);
        var report = await db.InterviewReports.SingleAsync(item => item.InterviewSessionId == interviewId);
        Assert.Equal("deterministic:validated-answer-aggregate-v1", report.ModelVersion);
        Assert.Equal("interview-report-fallback-v1", report.PromptVersion);
        var rubric = JsonSerializer.Deserialize<RubricScore[]>(report.Rubric, JsonOptions)!;
        Assert.Equal([75, 70, 65, 80], rubric.Select(item => item.Score));
        Assert.All(rubric, item => Assert.False(string.IsNullOrWhiteSpace(item.Evidence)));
        Assert.Equal(
            BillingValues.Processed,
            (await db.OutboxEvents
                .Where(item => item.AggregateId == interviewId && item.Type == "InterviewReportRequested")
                .ToArrayAsync())
                .OrderByDescending(item => item.CreatedAt)
                .ThenByDescending(item => item.Id)
                .First().Status);
    }

    [Fact]
    public async Task ReportMalformedTwiceUsesValidatedAnswerAggregateFallback()
    {
        var aiProvider = new TestAiProvider();
        var malformed = new AiProviderException(
            AiProviderFailureKind.InvalidResponse,
            "Malformed report response.",
            retryHint: AiProviderRetryHint.MalformedStructuredOutput);
        aiProvider.EnqueueResponse(AiPurposes.InterviewReport, malformed);
        aiProvider.EnqueueResponse(AiPurposes.InterviewReport, malformed);
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "malformed-report-start");
        await ProcessJobsAsync(factory);
        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestionId = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestionId, "Tôi đã phân tích nguyên nhân và xử lý sự cố.", "malformed-report-answer-one");
        var secondQuestionId = firstAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, secondQuestionId, "Kết quả là hệ thống ổn định hơn.", "malformed-report-answer-two");
        var afterSecondAnswer = await GetInterviewAsync(client, interviewId);
        var thirdQuestionId = afterSecondAnswer.GetProperty("questions").EnumerateArray().Last().GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, thirdQuestionId, "Tôi tiếp tục theo dõi chỉ số sau thay đổi.", "malformed-report-answer-three");
        await CompleteAsync(client, interviewId, "malformed-report-complete");

        await ProcessJobsAsync(factory);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewReport));
        var report = await db.InterviewReports.SingleAsync(item => item.InterviewSessionId == interviewId);
        Assert.Equal("deterministic:validated-answer-aggregate-v1", report.ModelVersion);
        Assert.Equal("interview-report-fallback-v1", report.PromptVersion);
    }

    [Theory]
    [InlineData("Authentication", 1)]
    [InlineData("Configuration", 1)]
    [InlineData("RateLimited", 2)]
    [InlineData("Unavailable", 2)]
    [InlineData("Timeout", 2)]
    public async Task ReportProviderFailuresNeverUseValidatedAnswerAggregateFallback(
        string failureKindName,
        int expectedProviderCalls)
    {
        var aiProvider = new TestAiProvider();
        var failureKind = Enum.Parse<AiProviderFailureKind>(failureKindName);
        for (var attempt = 0; attempt < expectedProviderCalls; attempt++)
        {
            aiProvider.EnqueueResponse(
                AiPurposes.InterviewReport,
                new AiProviderException(failureKind, $"{failureKindName} report failure."));
        }

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, $"provider-failure-report-{failureKindName.ToLowerInvariant()}");
        await ProcessJobsAsync(factory);
        var active = await GetInterviewAsync(client, interviewId);
        var firstQuestionId = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestionId, "Tôi đã phân tích nguyên nhân và xử lý sự cố.", $"provider-failure-answer-one-{failureKindName}");
        var secondQuestionId = firstAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, secondQuestionId, "Kết quả là hệ thống ổn định hơn.", $"provider-failure-answer-two-{failureKindName}");
        var afterSecondAnswer = await GetInterviewAsync(client, interviewId);
        var thirdQuestionId = afterSecondAnswer.GetProperty("questions").EnumerateArray().Last().GetProperty("id").GetGuid();
        await AnswerAsync(client, interviewId, thirdQuestionId, "Tôi tiếp tục theo dõi chỉ số sau thay đổi.", $"provider-failure-answer-three-{failureKindName}");
        await CompleteAsync(client, interviewId, $"provider-failure-report-complete-{failureKindName}");

        await ProcessJobsAsync(factory);

        Assert.Equal(expectedProviderCalls, aiProvider.GetCallCount(AiPurposes.InterviewReport));
        Assert.Equal(
            InterviewReportStates.Failed,
            (await GetInterviewAsync(client, interviewId)).GetProperty("reportState").GetString());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(PracticeValues.Completing, (await db.InterviewSessions.SingleAsync(item => item.Id == interviewId)).Status);
        Assert.Empty(await db.InterviewReports.Where(item => item.InterviewSessionId == interviewId).ToArrayAsync());
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
        for (var pass = 0; pass < 10; pass++)
        {
            using var scope = factory.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>()
                    .ProcessPendingAsync(CancellationToken.None) == 0)
                return;
        }

        throw new InvalidOperationException("Practice jobs did not drain.");
    }

    private static async Task<JsonElement> ProcessAndGetEvaluationAsync(
        NexoraApiFactory factory,
        JsonElement answerResult)
    {
        await ProcessJobsAsync(factory);
        var answerId = answerResult.GetProperty("answer").GetProperty("id").GetGuid();
        using var scope = factory.Services.CreateScope();
        var evaluation = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>()
            .InterviewAnswers.AsNoTracking()
            .Where(item => item.Id == answerId)
            .Select(item => item.Evaluation)
            .SingleAsync();
        Assert.False(string.IsNullOrWhiteSpace(evaluation));
        using var document = JsonDocument.Parse(evaluation);
        return document.RootElement.Clone();
    }

    private static async Task<Guid> SeedReadyResumeWithProfileAsync(
        NexoraApiFactory factory,
        Guid userId,
        TestAiProvider aiProvider,
        string marker)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = $"practice/{Guid.NewGuid():N}",
            FileName = "active-interview-context.pdf",
            ContentType = "application/pdf",
            Size = 100,
            Checksum = Guid.NewGuid().ToString("N"),
            CreatedAt = now
        };
        var profile = new ResumeProfile($"Candidate profile {marker}", [$"Skill {marker}"], [], [], [], [], []);
        var resume = new ResumeRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StoredFileId = file.Id,
            Status = PracticeValues.Ready,
            Version = 1,
            StructuredProfile = JsonSerializer.Serialize(profile, JsonOptions),
            ProfileModelVersion = aiProvider.ModelVersion,
            ProfilePromptVersion = AiOperations.ResumeProfile.PromptVersion,
            ProfileSchemaVersion = AiOperations.ResumeProfile.SchemaVersion,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(file, resume);
        await db.SaveChangesAsync();
        return resume.Id;
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

    private static async Task SeedEntitlementAsync(
        NexoraApiFactory factory,
        Guid userId,
        int quota,
        int? questionLimit = null,
        bool unlimitedQuestionLimit = false)
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
        if (questionLimit is not null || unlimitedQuestionLimit)
        {
            var definition = await db.FeatureDefinitions.SingleAsync(item => item.Code == FeatureValues.InterviewQuestionLimit);
            db.EntitlementFeatures.Add(new EntitlementFeature
            {
                Id = Guid.NewGuid(),
                EntitlementId = entitlement.Id,
                FeatureDefinitionId = definition.Id,
                FeatureCode = definition.Code,
                IsEnabled = true,
                Limit = unlimitedQuestionLimit ? null : questionLimit,
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

        Guid q4;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var starPrimary = await db.InterviewQuestions.SingleAsync(item => item.Id == q2);
            var followup = await db.InterviewQuestions.SingleAsync(item =>
                item.InterviewSessionId == interviewId && item.Sequence == 4);
            followup.Kind = InterviewQuestionValues.Followup;
            followup.Topic = InterviewQuestionValues.BehavioralStar;
            followup.ParentQuestionId = starPrimary.Id;
            followup.Content = "Explicit STAR follow-up.";
            q4 = followup.Id;
            await db.SaveChangesAsync();
        }
        Assert.Equal(q4, q3Result.GetProperty("nextQuestion").GetProperty("id").GetGuid());
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
