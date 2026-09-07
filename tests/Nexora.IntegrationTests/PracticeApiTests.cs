using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class PracticeApiTests
{
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
        var firstQuestion = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var firstAnswer = await AnswerAsync(client, interviewId, firstQuestion, "Tôi phân tích nguyên nhân, phối hợp đội và giảm 30% lỗi.", "answer-one");
        var firstStar = firstAnswer.GetProperty("answer").GetProperty("evaluation").GetProperty("star");
        Assert.True(firstStar.GetProperty("applicable").GetBoolean());
        Assert.Equal(56, firstStar.GetProperty("overallScore").GetInt32());
        Assert.False(firstStar.GetProperty("result").GetProperty("detected").GetBoolean());
        Assert.Contains(firstStar.GetProperty("missingElements").EnumerateArray(), item => item.GetString() == "result");
        var secondQuestion = firstAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();

        var refreshed = await GetInterviewAsync(client, interviewId);
        Assert.Equal(2, refreshed.GetProperty("questions").GetArrayLength());
        Assert.Single(refreshed.GetProperty("answers").EnumerateArray());
        var secondAnswer = await AnswerAsync(client, interviewId, secondQuestion, "Tôi sẽ đo baseline sớm hơn và kiểm tra theo tuần.", "answer-two");
        Assert.True(secondAnswer.GetProperty("isComplete").GetBoolean());

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
        var starSummary = report.GetProperty("starSummary");
        Assert.Equal(2, starSummary.GetProperty("applicableAnswers").GetInt32());
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
        await CompleteAsync(client, interviewId, "report-complete-one");
        await ProcessJobsAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(PracticeValues.Completing, (await db.InterviewSessions.SingleAsync(item => item.Id == interviewId)).Status);
            Assert.Equal(1, (await db.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free")).Adjustment);
            Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.Action == BillingValues.Adjustment && item.SourceType == "report_failure"));
        }

        await CompleteAsync(client, interviewId, "report-complete-retry");
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
            email = $"practice-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "Practice candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var data = await DataAsync(response);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);

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
