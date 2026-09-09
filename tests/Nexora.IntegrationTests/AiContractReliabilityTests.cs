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
            AiOperations.ScoreScale));

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
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale));

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
    public async Task OverlongFollowupRepairsOnceThenUsesFallbackWithoutDiscardingAnswer()
    {
        var aiProvider = new TestAiProvider();
        aiProvider.EnqueueResponse(AiPurposes.InterviewEvaluate, new AnswerEvaluation(
            [
                new RubricScore("correctness", 80, "Grounded evidence."),
                new RubricScore("structure", 80, "Grounded evidence."),
                new RubricScore("completeness", 80, "Grounded evidence."),
                new RubricScore("clarity", 80, "Grounded evidence.")
            ],
            "Grounded feedback.",
            new StarEvaluation(false, null, null, null, null, null, [], [], []),
            AiOperations.ScoreScale));
        var overlong = new GeneratedQuestion(new string('x', 2_001));
        aiProvider.EnqueueResponse(AiPurposes.InterviewFollowup, overlong);
        aiProvider.EnqueueResponse(AiPurposes.InterviewFollowup, overlong);
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        await SeedEntitlementAsync(factory, account.UserId, 5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var interviewId = await StartInterviewAsync(client, "technical", "overlong-followup");
        await ProcessJobsAsync(factory);
        var interview = await GetInterviewAsync(client, interviewId);
        var questionId = interview.GetProperty("questions")[0].GetProperty("id").GetGuid();

        using var answerRequest = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{interviewId}/answers")
        {
            Content = JsonContent.Create(new
            {
                questionId,
                content = "Dependency injection supplies dependencies from outside the class.",
                durationSeconds = 45
            })
        };
        answerRequest.Headers.Add("Idempotency-Key", "overlong-followup-answer");
        using var answerResponse = await client.SendAsync(answerRequest);

        Assert.Equal(HttpStatusCode.OK, answerResponse.StatusCode);
        var data = await DataAsync(answerResponse);
        var fallback = data.GetProperty("nextQuestion").GetProperty("content").GetString();
        Assert.False(string.IsNullOrWhiteSpace(fallback));
        Assert.True(fallback!.Length <= 2_000);
        Assert.Equal(2, aiProvider.GetCallCount(AiPurposes.InterviewFollowup));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.InterviewAnswers.CountAsync(item => item.QuestionId == questionId));
        Assert.Equal(2, await db.InterviewQuestions.CountAsync(item => item.InterviewSessionId == interviewId));
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
            AiOperations.ScoreScale));

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
            AiOperations.ScoreScale));

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
    public async Task RegressionFixturesFollowUpAwareEvaluationPreservesAllDetectedStarComponents()
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
            AiOperations.ScoreScale));

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
            AiOperations.ScoreScale));

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

    private sealed record Account(Guid UserId, string AccessToken);
}
