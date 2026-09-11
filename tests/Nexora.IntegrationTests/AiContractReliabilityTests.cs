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
                CoachingTips: []),
            AiOperations.ScoreScale,
            Strengths: ["Dependency injection is explained clearly."],
            Improvements: ["Add one concrete example if available."],
            ImprovedAnswer: "Dependency injection is a design pattern in which an object receives other objects that it depends on."));

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
    public async Task FreePrimaryAnswerDoesNotGenerateAdaptiveFollowup()
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
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            Strengths: ["The answer explains maintainable software architecture."],
            Improvements: ["Add one concrete example if available."],
            ImprovedAnswer: "Solid principles help build maintainable software architecture."));

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
        // The next free question is the next canonical primary, not an adaptive follow-up.
        Assert.True(data.TryGetProperty("nextQuestion", out var nextQuestionElement));
        var nextQuestionContent = nextQuestionElement.GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(nextQuestionContent));
        Assert.True(nextQuestionContent!.Length <= 2_000);
        Assert.Equal(InterviewQuestionValues.Primary, nextQuestionElement.GetProperty("kind").GetString());
        Assert.Equal(InterviewQuestionValues.BehavioralStar, nextQuestionElement.GetProperty("topic").GetString());
        Assert.Equal(0, aiProvider.GetCallCount(AiPurposes.InterviewFollowup));

        // Verify in DB that answer was persisted
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var persistedAnswer = await db.InterviewAnswers.SingleOrDefaultAsync(a => a.QuestionId == questionId);
        Assert.NotNull(persistedAnswer);
    }

    [Fact]
    public async Task OverlongPaidContinuationQuestionRepairsOnceThenFailsWithoutPersistingQuestion() =>
        await PaidContinuationOverlongQuestionFailsAfterTwoAttemptsAsync();

    private static async Task PaidContinuationOverlongQuestionFailsAfterTwoAttemptsAsync()
    {
        var aiProvider = new TestAiProvider();
        var overlong = new GeneratedQuestion(new string('x', 2_001));
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5, questionLimit: 6);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "technical", "overlong-followup");
        await ProcessJobsAsync(factory);

        var interview = await GetInterviewAsync(client, interviewId);
        var q1 = interview.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var q2 = (await SubmitAnswerAsync(client, interviewId, q1, "First answer.", "overlong-answer-one"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3 = (await SubmitAnswerAsync(client, interviewId, q2, "Second answer.", "overlong-answer-two"))
            .GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var q3Result = await SubmitAnswerAsync(client, interviewId, q3, "Third answer.", "overlong-answer-three");
        Assert.Equal(JsonValueKind.Null, q3Result.GetProperty("nextQuestion").ValueKind);

        var firstQuestionCallsBeforeContinuation = aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion);
        aiProvider.EnqueueResponse(AiPurposes.InterviewFirstQuestion, overlong);
        aiProvider.EnqueueResponse(AiPurposes.InterviewFirstQuestion, overlong);
        using var continueRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/continue");
        continueRequest.Headers.Add("Idempotency-Key", "overlong-continuation");
        using var continueResponse = await client.SendAsync(continueRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, continueResponse.StatusCode);
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewFirstQuestion) - firstQuestionCallsBeforeContinuation);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(3, await db.InterviewAnswers.CountAsync(item => item.InterviewSessionId == interviewId));
        Assert.Equal(3, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
    }

    private static async Task<JsonElement> SubmitAnswerAsync(
        HttpClient client, Guid interviewId, Guid questionId, string content, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new { questionId, content, durationSeconds = 45 })
        };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await DataAsync(response);
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
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            Strengths: ["Polymorphism is clear."],
            Improvements: ["Add one concrete example if available."],
            ImprovedAnswer: "Polymorphism enables treating objects of different types through a common interface."));

        // Attempt 2: valid rubric (all 4 criteria)
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Good answer."),
                new RubricScore("structure", 80, "Clear structure."),
                new RubricScore("completeness", 80, "Complete response."),
                new RubricScore("clarity", 85, "Very clear.")
            ],
            "Good job.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            Strengths: ["Polymorphism is clear."],
            Improvements: ["Add one concrete example if available."],
            ImprovedAnswer: "Polymorphism enables treating objects of different types through a common interface."));

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
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale));
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [new RubricScore("correctness", 80, "Good answer.")],
            "Still incomplete rubric",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale));

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

    [Fact]
    public async Task NullRubricItemMapsToSafe503AndDoesNotPersistAnswer()
    {
        var aiProvider = new TestAiProvider();
        var malformed = new AnswerEvaluation(
            [
                null!,
                new RubricScore("structure", 80, "Clear structure."),
                new RubricScore("completeness", 80, "Complete response."),
                new RubricScore("clarity", 80, "Clear communication.")
            ],
            "Malformed rubric",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale,
            Strengths: ["Grounded answer"],
            Improvements: ["Add one concrete example if available."],
            ImprovedAnswer: "Keep the same answer and add concrete evidence if available.");
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, malformed);
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, malformed);

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "technical", "null-rubric-item");
        await ProcessJobsAsync(factory);
        var questionId = (await GetInterviewAsync(client, interviewId)).GetProperty("questions")[0].GetProperty("id").GetGuid();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new { questionId, content = "A grounded answer.", durationSeconds = 30 })
        };
        request.Headers.Add("Idempotency-Key", "null-rubric-item-answer");
        using var response = await client.SendAsync(request);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("AI_OUTPUT_INVALID", body, StringComparison.Ordinal);
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewEvaluate));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await db.InterviewAnswers.CountAsync(item => item.QuestionId == questionId));
    }
    [Fact]
    public async Task AnswerCoachingPersistsAndIdempotentReplayDoesNotReevaluate()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "technical", "coaching-replay");
        await ProcessJobsAsync(factory);
        var questionId = (await GetInterviewAsync(client, interviewId)).GetProperty("questions")[0].GetProperty("id").GetGuid();
        const string answer = "I debugged the API.";

        var first = await SubmitAnswerAsync(client, interviewId, questionId, answer, "coaching-replay-answer");
        var firstEvaluation = first.GetProperty("answer").GetProperty("evaluation");
        Assert.Equal(JsonValueKind.Array, firstEvaluation.GetProperty("strengths").ValueKind);
        Assert.NotEmpty(firstEvaluation.GetProperty("strengths").EnumerateArray());
        Assert.NotEmpty(firstEvaluation.GetProperty("improvements").EnumerateArray());
        Assert.Equal(answer, firstEvaluation.GetProperty("improvedAnswer").GetString());
        var evaluationCalls = aiProvider.GetCallCount(AiPurposes.InterviewEvaluate);

        var replay = await SubmitAnswerAsync(client, interviewId, questionId, answer, "coaching-replay-answer");
        var replayEvaluation = replay.GetProperty("answer").GetProperty("evaluation");
        Assert.Equal(evaluationCalls, aiProvider.GetCallCount(AiPurposes.InterviewEvaluate));
        Assert.Equal(firstEvaluation.GetProperty("strengths").GetRawText(), replayEvaluation.GetProperty("strengths").GetRawText());
        Assert.Equal(firstEvaluation.GetProperty("improvements").GetRawText(), replayEvaluation.GetProperty("improvements").GetRawText());
        Assert.Equal(firstEvaluation.GetProperty("improvedAnswer").GetString(), replayEvaluation.GetProperty("improvedAnswer").GetString());
    }

    [Fact]
    public async Task FabricatedCoachingRepairsOnceAndPersistsOnlyCorrectedOutput()
    {
        var aiProvider = new TestAiProvider();
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, ApiCoachingEvaluation(
            "I debugged the API and mentored the team through a RabbitMQ migration."));
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, ApiCoachingEvaluation("I debugged the API."));

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "technical", "coaching-repair");
        await ProcessJobsAsync(factory);
        var questionId = (await GetInterviewAsync(client, interviewId)).GetProperty("questions")[0].GetProperty("id").GetGuid();

        var data = await SubmitAnswerAsync(client, interviewId, questionId, "I debugged the API.", "coaching-repair-answer");
        var evaluation = data.GetProperty("answer").GetProperty("evaluation");
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewEvaluate));
        Assert.Equal("I debugged the API.", evaluation.GetProperty("improvedAnswer").GetString());
        Assert.DoesNotContain("RabbitMQ", evaluation.GetProperty("improvedAnswer").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PersistentFabricatedCoachingFailsClosedWithoutPersistingAnswer()
    {
        var aiProvider = new TestAiProvider();
        var fabricated = ApiCoachingEvaluation("I debugged the API and mentored the team through a RabbitMQ migration.");
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, fabricated);
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, fabricated);

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "technical", "coaching-terminal");
        await ProcessJobsAsync(factory);
        var questionId = (await GetInterviewAsync(client, interviewId)).GetProperty("questions")[0].GetProperty("id").GetGuid();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new { questionId, content = "I debugged the API.", durationSeconds = 45 })
        };
        request.Headers.Add("Idempotency-Key", "coaching-terminal-answer");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewEvaluate));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(0, await db.InterviewAnswers.CountAsync(item => item.QuestionId == questionId));
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

    [Fact]
    public void FollowUpContextTestMIncludesMetadataAndTargetElements()
    {
        var builder = new ResumeContextBuilder();
        var context = builder.BuildAnswerEvaluationContext(
            "Backend Developer",
            "junior",
            "behavioral",
            "JD content here",
            "Follow-up question?",
            "Answer here",
            null,
            questionSequence: 2,
            isFollowUp: true,
            followupTargetElements: ["task", "action"]);

        Assert.Contains("question-sequence: 2", context);
        Assert.Contains("is-follow-up: true", context);
        Assert.Contains("followup-target-elements: task; action", context);
        Assert.Contains("Still detect every STAR element present in the current answer", context);
    }

    [Fact]
    public async Task RegressionFixturesFollowUpAwareEvaluationPreservesAllDetectedStarComponents() =>
        await CanonicalPrimariesAndExplicitFollowupPreserveAllDetectedStarComponentsAsync();

    private static async Task CanonicalPrimariesAndExplicitFollowupPreserveAllDetectedStarComponentsAsync()
    {
        var aiProvider = new TestAiProvider();
        EnqueueGroundedEvaluation(aiProvider, EvaluationWithoutStar());
        EnqueueGroundedEvaluation(aiProvider, EvaluationWithStar(Star(85, 0, 85, 90, resultDetected: true)));
        EnqueueGroundedEvaluation(aiProvider, EvaluationWithoutStar());
        EnqueueGroundedEvaluation(aiProvider, EvaluationWithStar(Star(90, 90, 95, 90, resultDetected: true)));

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "behavioral", "regression-test-star");
        await ProcessJobsAsync(factory);

        var active = await GetInterviewAsync(client, interviewId);
        var q1 = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var a1 = await SubmitAnswerAsync(client, interviewId, q1, "First primary answer.", "ans-1-regression");
        Assert.False(a1.GetProperty("answer").GetProperty("evaluation").GetProperty("star").GetProperty("applicable").GetBoolean());

        var q2 = a1.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var a2 = await SubmitAnswerAsync(client, interviewId, q2, "STAR story with action and result.", "ans-2-regression");
        var a2Star = a2.GetProperty("answer").GetProperty("evaluation").GetProperty("star");
        Assert.True(a2Star.GetProperty("action").GetProperty("detected").GetBoolean());
        Assert.True(a2Star.GetProperty("result").GetProperty("detected").GetBoolean());
        Assert.False(a2Star.GetProperty("task").GetProperty("detected").GetBoolean());

        var q3 = a2.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await SubmitAnswerAsync(client, interviewId, q3, "Third primary answer.", "ans-3-regression");

        Guid q4;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var followup = new InterviewQuestion
            {
                Id = Guid.NewGuid(),
                InterviewSessionId = interviewId,
                Sequence = 4,
                Kind = InterviewQuestionValues.Followup,
                Topic = InterviewQuestionValues.BehavioralStar,
                ParentQuestionId = q2,
                Content = "Explicit STAR follow-up.",
                PromptVersion = "test-prompt",
                ModelVersion = "test-model",
                CreatedAt = DateTimeOffset.UtcNow
            };
            q4 = followup.Id;
            db.InterviewQuestions.Add(followup);
            await db.SaveChangesAsync();
        }

        var a4 = await SubmitAnswerAsync(client, interviewId, q4, "Follow-up adds the missing task and action details.", "ans-4-regression");
        var a4Star = a4.GetProperty("answer").GetProperty("evaluation").GetProperty("star");
        Assert.True(a4Star.GetProperty("situation").GetProperty("detected").GetBoolean());
        Assert.True(a4Star.GetProperty("task").GetProperty("detected").GetBoolean());
        Assert.True(a4Star.GetProperty("action").GetProperty("detected").GetBoolean());
        Assert.True(a4Star.GetProperty("result").GetProperty("detected").GetBoolean());
        Assert.Empty(a4Star.GetProperty("missingElements").EnumerateArray());
    }

    private static AnswerEvaluation EvaluationWithoutStar() => new(
        [
            new RubricScore("correctness", 80, "Grounded correctness evidence."),
            new RubricScore("structure", 80, "Grounded structure evidence."),
            new RubricScore("completeness", 80, "Grounded completeness evidence."),
            new RubricScore("clarity", 80, "Grounded clarity evidence.")
        ],
        "Grounded feedback.",
        new StarEvaluation(false, null, null, null, null, null, [], [], []),
        AiOperations.ScoreScale,
        ["Grounded answer"],
        ["Add one concrete example if available."],
        "Keep the same answer and add concrete evidence if available.");

    private static AnswerEvaluation EvaluationWithStar(StarEvaluation star) => new(
        [
            new RubricScore("correctness", 90, "Grounded correctness evidence."),
            new RubricScore("structure", 85, "Grounded structure evidence."),
            new RubricScore("completeness", 80, "Grounded completeness evidence."),
            new RubricScore("clarity", 85, "Grounded clarity evidence.")
        ],
        "Grounded feedback.", star, AiOperations.ScoreScale,
        ["Grounded answer"],
        ["Add one concrete example if available."],
        "Keep the same answer and add concrete evidence if available.");

    private static StarEvaluation Star(int situation, int task, int action, int result, bool resultDetected) => new(
        true,
        null,
        new StarComponentEvaluation(situation, true, "Situation evidence.", "Situation feedback."),
        task > 0
            ? new StarComponentEvaluation(task, true, "Task evidence.", "Task feedback.")
            : new StarComponentEvaluation(0, false, string.Empty, "Task feedback."),
        new StarComponentEvaluation(action, true, "Action evidence.", "Action feedback."),
        resultDetected
            ? new StarComponentEvaluation(result, true, "Result evidence.", "Result feedback.")
            : new StarComponentEvaluation(0, false, string.Empty, "Result feedback."),
        task > 0 ? [] : ["task"], [], [], AiOperations.ScoreScale);

    private static async Task LegacyRegressionFixturesFollowUpAwareEvaluationAsync()
    {
        var aiProvider = new TestAiProvider();

        // 1. First answer evaluation: Action & Result detected
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [
                new RubricScore("correctness", 90, "Thêm index B-tree và tích hợp Redis cache cho các mặt hàng hot giúp giảm latency từ 1.5s xuống 90ms."),
                new RubricScore("structure", 85, "Nêu rõ tình huống, hành động khắc phục và kết quả đạt được."),
                new RubricScore("completeness", 80, "Trả lời đầy đủ các ý về sự cố, cách xử lý và kết quả."),
                new RubricScore("clarity", 85, "Sử dụng thuật ngữ kỹ thuật rõ ràng.")
            ],
            "Ứng viên trình bày tốt cách xử lý sự cố kỹ thuật.",
            new StarEvaluation(
                Applicable: true,
                OverallScore: 78,
                Situation: new StarComponentEvaluation(85, true, "Trong dự án trước, hệ thống thanh toán gặp tình trạng database connection pool bị cạn kiệt trong đợt flash sale.", "Mô tả bối cảnh tốt."),
                Task: new StarComponentEvaluation(0, false, "", "Chưa nêu rõ trách nhiệm cụ thể của cá nhân."),
                Action: new StarComponentEvaluation(85, true, "Tôi đã kiểm tra slow query log, phát hiện query tìm kiếm SKU thiếu index. Sau đó tôi thêm index B-tree và tích hợp Redis cache", "Hành động kỹ thuật rõ ràng."),
                Result: new StarComponentEvaluation(90, true, "latency giảm từ 1.5s xuống còn 90ms và hệ thống không còn bị crash.", "Kết quả định lượng rõ ràng."),
                MissingElements: ["task"],
                Strengths: ["Xử lý kỹ thuật tốt"],
                CoachingTips: ["Bổ sung vai trò cá nhân"]),
            AiOperations.ScoreScale,
            Strengths: ["Xử lý kỹ thuật tốt"],
            Improvements: ["Bổ sung vai trò cá nhân nếu có."],
            ImprovedAnswer: "Tôi đã kiểm tra slow query log, phát hiện query tìm kiếm SKU thiếu index và thêm index B-tree."));

        // Followup question generation
        aiProvider.EnqueueResponse("interview.followup", new GeneratedQuestion(
            "Cảm ơn bạn đã chia sẻ về giải pháp tối ưu index và dùng Redis. Cụ thể thì vai trò và nhiệm vụ của riêng bạn trong việc phát hiện và xử lý sự cố này là gì?"));

        // 2. Second answer evaluation (Follow-up): Task, Action & Result detected
        aiProvider.EnqueueResponse("interview.evaluate", new AnswerEvaluation(
            [
                new RubricScore("correctness", 95, "tôi trực tiếp phân tích pg_stat_statements và chạy EXPLAIN ANALYZE, phát hiện Seq Scan do thiếu index, sau đó tôi viết script tạo index CONCURRENTLY và code tầng Redis cache."),
                new RubricScore("structure", 95, "Cấu trúc rõ ràng."),
                new RubricScore("completeness", 90, "Đầy đủ các phần."),
                new RubricScore("clarity", 90, "giải pháp được release an toàn sau 3 giờ, hệ thống chịu tải tốt.")
            ],
            "Ứng viên trả lời rất rõ ràng, nêu bật vai trò cá nhân và sự phối hợp hiệu quả.",
            new StarEvaluation(
                Applicable: true,
                OverallScore: 92,
                Situation: new StarComponentEvaluation(90, true, "Khi Grafana cảnh báo P99 latency vượt ngưỡng, nhiệm vụ của tôi là khoanh vùng root cause...", "Nêu bối cảnh sự cố tốt."),
                Task: new StarComponentEvaluation(90, true, "nhiệm vụ của tôi là khoanh vùng root cause và đưa ra giải pháp không gây gián đoạn dịch vụ.", "Trách nhiệm cá nhân rõ."),
                Action: new StarComponentEvaluation(95, true, "tôi trực tiếp phân tích pg_stat_statements và chạy EXPLAIN ANALYZE, phát hiện Seq Scan do thiếu index, sau đó tôi viết script tạo index CONCURRENTLY và code tầng Redis cache.", "Hành động kỹ thuật cụ thể."),
                Result: new StarComponentEvaluation(90, true, "giải pháp được release an toàn sau 3 giờ, hệ thống chịu tải tốt và tôi đã đóng góp tài liệu RCA/Runbook vào wiki nội bộ của team.", "Kết quả cụ thể và tài liệu hóa."),
                MissingElements: [],
                Strengths: ["Kỹ năng phân tích nguyên nhân gốc rễ xuất sắc"],
                CoachingTips: []),
            AiOperations.ScoreScale,
            Strengths: ["Kỹ năng phân tích nguyên nhân gốc rễ xuất sắc"],
            Improvements: ["Bổ sung một kết quả cụ thể nếu có."],
            ImprovedAnswer: "Tôi trực tiếp phân tích pg_stat_statements và chạy EXPLAIN ANALYZE để tìm nguyên nhân."));

        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var interviewId = await StartInterviewAsync(client, "behavioral", "regression-test-star");
        await ProcessJobsAsync(factory);

        var interview = await GetInterviewAsync(client, interviewId);
        var q1Id = interview.GetProperty("questions")[0].GetProperty("id").GetGuid();

        // Submit Answer 1
        using var a1Req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new
            {
                questionId = q1Id,
                content = "Trong dự án trước, hệ thống thanh toán gặp tình trạng database connection pool bị cạn kiệt trong đợt flash sale. Tôi đã kiểm tra slow query log, phát hiện query tìm kiếm SKU thiếu index. Sau đó tôi thêm index B-tree và tích hợp Redis cache cho các mặt hàng hot. Kết quả là latency giảm từ 1.5s xuống còn 90ms và hệ thống không còn bị crash.",
                durationSeconds = 55
            })
        };
        a1Req.Headers.Add("Idempotency-Key", "ans-1-regression");
        using var a1Resp = await client.SendAsync(a1Req);
        Assert.Equal(HttpStatusCode.OK, a1Resp.StatusCode);

        var a1Data = await DataAsync(a1Resp);
        var a1Star = a1Data.GetProperty("answer").GetProperty("evaluation").GetProperty("star");
        Assert.True(a1Star.GetProperty("action").GetProperty("detected").GetBoolean());
        Assert.True(a1Star.GetProperty("result").GetProperty("detected").GetBoolean());
        Assert.False(a1Star.GetProperty("task").GetProperty("detected").GetBoolean());
        Assert.Contains("task", a1Star.GetProperty("missingElements").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("action", a1Star.GetProperty("missingElements").EnumerateArray().Select(e => e.GetString()));
        Assert.DoesNotContain("result", a1Star.GetProperty("missingElements").EnumerateArray().Select(e => e.GetString()));

        var q2Id = a1Data.GetProperty("nextQuestion").GetProperty("id").GetGuid();

        // Submit Answer 2 (Follow-up)
        using var a2Req = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new
            {
                questionId = q2Id,
                content = "Trong sự cố đó, tôi là Backend Developer trực chiến on-call phụ trách module Kho và Đơn hàng. Khi Grafana cảnh báo P99 latency vượt ngưỡng, nhiệm vụ của tôi là khoanh vùng root cause và đưa ra giải pháp không gây gián đoạn dịch vụ. Về cá nhân: tôi trực tiếp phân tích pg_stat_statements và chạy EXPLAIN ANALYZE, phát hiện Seq Scan do thiếu index, sau đó tôi viết script tạo index CONCURRENTLY và code tầng Redis cache. Về phối hợp: tôi trao đổi với Tech Lead về trade-off tài nguyên; làm việc với DBA để kiểm duyệt plan migration vào giờ thấp điểm; và phối hợp cùng QA load-test trên Staging để đảm bảo dữ liệu nhất quán. Nhờ đó, giải pháp được release an toàn sau 3 giờ, hệ thống chịu tải tốt và tôi đã đóng góp tài liệu RCA/Runbook vào wiki nội bộ của team.",
                durationSeconds = 65
            })
        };
        a2Req.Headers.Add("Idempotency-Key", "ans-2-regression");
        using var a2Resp = await client.SendAsync(a2Req);
        Assert.Equal(HttpStatusCode.OK, a2Resp.StatusCode);

        var a2Data = await DataAsync(a2Resp);
        var a2Star = a2Data.GetProperty("answer").GetProperty("evaluation").GetProperty("star");
        Assert.True(a2Star.GetProperty("situation").GetProperty("detected").GetBoolean());
        Assert.True(a2Star.GetProperty("task").GetProperty("detected").GetBoolean());
        Assert.True(a2Star.GetProperty("action").GetProperty("detected").GetBoolean());
        Assert.True(a2Star.GetProperty("result").GetProperty("detected").GetBoolean());
        Assert.Empty(a2Star.GetProperty("missingElements").EnumerateArray());
    }

    private static async Task ProcessJobsAsync(NexoraApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>().ProcessPendingAsync(CancellationToken.None);
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
        var email = $"ai-test-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "AI Test Candidate"
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
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("data").Clone();
    }

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

    private static AnswerEvaluation ApiCoachingEvaluation(string improvedAnswer) => new(
        [
            new RubricScore("correctness", 80, "The API debugging approach is described."),
            new RubricScore("structure", 80, "The answer is ordered."),
            new RubricScore("completeness", 80, "The API issue is covered."),
            new RubricScore("clarity", 80, "The answer is clear.")
        ],
        "Good answer.",
        new StarEvaluation(false, null, null, null, null, null, [], [], []),
        AiOperations.ScoreScale,
        ["The API debugging is clear."],
        ["Add one concrete result if available."],
        improvedAnswer);

    private sealed record Account(Guid UserId, string AccessToken);
}
