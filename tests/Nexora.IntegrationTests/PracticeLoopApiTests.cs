using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

public sealed class PracticeLoopApiTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PracticeLoopReadEndpointsRequireAuthenticationAndReturnSafeEmptyResults()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/interviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/resume-analyses")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/job-descriptions")).StatusCode);

        var account = await RegisterAsync(client, "Empty practice loop candidate");
        Authorize(client, account);

        using var interviews = await client.GetAsync("/api/v1/interviews?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, interviews.StatusCode);
        var interviewData = await DataAsync(interviews);
        Assert.Empty(interviewData.GetProperty("items").EnumerateArray());
        Assert.Equal(0, interviewData.GetProperty("totalCount").GetInt32());
        Assert.False(interviewData.GetProperty("hasNextPage").GetBoolean());

        using var analyses = await client.GetAsync("/api/v1/resume-analyses?page=1&pageSize=20");
        Assert.Equal(HttpStatusCode.OK, analyses.StatusCode);
        Assert.Empty((await DataAsync(analyses)).GetProperty("items").EnumerateArray());

        using var jobDescriptions = await client.GetAsync("/api/v1/job-descriptions");
        Assert.Equal(HttpStatusCode.OK, jobDescriptions.StatusCode);
        Assert.Empty((await DataAsync(jobDescriptions)).EnumerateArray());
    }

    [Fact]
    public async Task GoalBasedInterviewStartUsesPrimaryResumeAndSnapshotsResolvedContext()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "Goal context candidate");
        Authorize(client, account);

        var resumeId = await SeedReadyResumeAsync(factory, account.UserId, "primary.pdf", At(1));
        using (var primary = await client.PutAsJsonAsync("/api/v1/me/primary-resume", new { resumeId }))
        {
            Assert.Equal(HttpStatusCode.OK, primary.StatusCode);
        }

        var jobDescriptionId = await CreateJobDescriptionAsync(client, "Platform JD", "Private platform job description.");
        var careerGoalId = await CreateCareerGoalAsync(client, "Platform Engineer", "senior", jobDescriptionId);
        await SeedEntitlementAsync(factory, account.UserId, 2);

        using var start = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new
            {
                careerGoalId,
                interviewType = "technical",
                difficulty = "medium"
            })
        };
        start.Headers.Add("Idempotency-Key", "goal-context-start");
        using var startResponse = await client.SendAsync(start);
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var started = await DataAsync(startResponse);
        var interviewId = started.GetProperty("id").GetGuid();
        Assert.Equal("Platform Engineer", started.GetProperty("role").GetString());
        Assert.Equal("senior", started.GetProperty("seniority").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var session = await db.InterviewSessions.AsNoTracking().SingleAsync(item => item.Id == interviewId);
            Assert.Equal(account.UserId, session.UserId);
            Assert.Equal(careerGoalId, session.CareerGoalId);
            Assert.Equal(resumeId, session.ResumeId);
            Assert.Equal(jobDescriptionId, session.JobDescriptionId);
            Assert.Equal("Platform Engineer", session.Role);
            Assert.Equal("senior", session.Seniority);
        }

        using (var changedGoal = await PatchAsync(client, $"/api/v1/career-goals/{careerGoalId}", new
               {
                   targetRole = "Principal Engineer",
                   seniority = "mid",
                   targetJobDescriptionId = (Guid?)null
               }))
        {
            Assert.Equal(HttpStatusCode.OK, changedGoal.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var session = await db.InterviewSessions.AsNoTracking().SingleAsync(item => item.Id == interviewId);
            Assert.Equal("Platform Engineer", session.Role);
            Assert.Equal("senior", session.Seniority);
            Assert.Equal(jobDescriptionId, session.JobDescriptionId);
        }

        using var overrideStart = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(new
            {
                careerGoalId,
                role = "Staff Platform Engineer",
                seniority = "mid",
                interviewType = "technical",
                difficulty = "hard",
                resumeId,
                jobDescriptionId
            })
        };
        overrideStart.Headers.Add("Idempotency-Key", "goal-context-explicit-overrides");
        using var overrideResponse = await client.SendAsync(overrideStart);
        Assert.Equal(HttpStatusCode.Created, overrideResponse.StatusCode);
        var overridden = await DataAsync(overrideResponse);
        var overriddenId = overridden.GetProperty("id").GetGuid();
        Assert.Equal("Staff Platform Engineer", overridden.GetProperty("role").GetString());
        Assert.Equal("mid", overridden.GetProperty("seniority").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var session = await db.InterviewSessions.AsNoTracking().SingleAsync(item => item.Id == overriddenId);
            Assert.Equal(careerGoalId, session.CareerGoalId);
            Assert.Equal(resumeId, session.ResumeId);
            Assert.Equal(jobDescriptionId, session.JobDescriptionId);
            Assert.Equal("Staff Platform Engineer", session.Role);
            Assert.Equal("mid", session.Seniority);
        }
    }

    [Fact]
    public async Task GoalAndContextReferencesAreOwnerScoped()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "Goal owner");
        var other = await RegisterAsync(otherClient, "Goal other");
        Authorize(ownerClient, owner);
        Authorize(otherClient, other);

        var ownerJdId = await CreateJobDescriptionAsync(ownerClient, "Owner JD", "Owner-only content.");
        var otherJdId = await CreateJobDescriptionAsync(otherClient, "Other JD", "Other-only content.");
        var ownerResumeId = await SeedReadyResumeAsync(factory, owner.UserId, "owner.pdf", At(1));
        var otherResumeId = await SeedReadyResumeAsync(factory, other.UserId, "other.pdf", At(1));
        var ownerGoalId = await CreateCareerGoalAsync(ownerClient, "Backend Engineer", "junior", ownerJdId);
        var otherGoalId = await CreateCareerGoalAsync(otherClient, "Data Engineer", "senior", otherJdId);

        using (var foreignGoal = await StartAsync(ownerClient, new
               {
                   careerGoalId = otherGoalId,
                   interviewType = "technical",
                   difficulty = "medium"
               }, "foreign-career-goal"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignGoal.StatusCode);
            Assert.Equal("CAREER_GOAL_NOT_FOUND", await ErrorCodeAsync(foreignGoal));
        }

        using (var foreignResume = await StartAsync(ownerClient, new
               {
                   careerGoalId = ownerGoalId,
                   interviewType = "technical",
                   difficulty = "medium",
                   resumeId = otherResumeId
               }, "foreign-resume"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignResume.StatusCode);
        }

        using (var foreignJobDescription = await StartAsync(ownerClient, new
               {
                   careerGoalId = ownerGoalId,
                   interviewType = "technical",
                   difficulty = "medium",
                   jobDescriptionId = otherJdId
               }, "foreign-job-description"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignJobDescription.StatusCode);
        }

        Assert.NotEqual(ownerResumeId, otherResumeId);
    }

    [Fact]
    public async Task HistoryAndJobDescriptionNavigationAreOwnerScopedOrderedAndBounded()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var ownerClient = factory.CreateHttpsClient();
        using var otherClient = factory.CreateHttpsClient();
        var owner = await RegisterAsync(ownerClient, "History owner");
        var other = await RegisterAsync(otherClient, "History other");
        Authorize(ownerClient, owner);
        Authorize(otherClient, other);

        var firstJdId = await CreateJobDescriptionAsync(ownerClient, "Old JD", "owner-old-content");
        var secondJdId = await CreateJobDescriptionAsync(ownerClient, "New JD", "owner-new-content");
        var foreignJdId = await CreateJobDescriptionAsync(otherClient, "Foreign JD", "foreign-private-content");
        await SetJobDescriptionDatesAsync(factory, firstJdId, At(1));
        await SetJobDescriptionDatesAsync(factory, secondJdId, At(2));

        using (var ownerJds = await ownerClient.GetAsync("/api/v1/job-descriptions"))
        {
            Assert.Equal(HttpStatusCode.OK, ownerJds.StatusCode);
            var items = (await DataAsync(ownerJds)).EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal(secondJdId, items[0].GetProperty("id").GetGuid());
            Assert.DoesNotContain(foreignJdId, items.Select(item => item.GetProperty("id").GetGuid()));
            Assert.Contains("owner-new-content", items[0].GetProperty("content").GetString(), StringComparison.Ordinal);
            Assert.DoesNotContain("foreign-private-content", await ownerJds.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var foreignDetail = await ownerClient.GetAsync($"/api/v1/job-descriptions/{foreignJdId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreignDetail.StatusCode);
        }

        await SeedEntitlementAsync(factory, owner.UserId, 3);
        var firstInterviewId = await StartInterviewIdAsync(ownerClient, "history-one");
        var secondInterviewId = await StartInterviewIdAsync(ownerClient, "history-two");
        var thirdInterviewId = await StartInterviewIdAsync(ownerClient, "history-three");
        await SeedEntitlementAsync(factory, other.UserId, 1);
        var foreignInterviewId = await StartInterviewIdAsync(otherClient, "history-foreign");
        await SetInterviewDatesAsync(factory, firstInterviewId, At(1));
        await SetInterviewDatesAsync(factory, secondInterviewId, At(2));
        await SetInterviewDatesAsync(factory, thirdInterviewId, At(3));

        using (var firstPage = await ownerClient.GetAsync("/api/v1/interviews?page=1&pageSize=2"))
        {
            Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
            var data = await DataAsync(firstPage);
            var items = data.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(2, items.Length);
            Assert.Equal(thirdInterviewId, items[0].GetProperty("id").GetGuid());
            Assert.Equal(secondInterviewId, items[1].GetProperty("id").GetGuid());
            Assert.Equal(3, data.GetProperty("totalCount").GetInt32());
            Assert.True(data.GetProperty("hasNextPage").GetBoolean());
            var body = await firstPage.Content.ReadAsStringAsync();
            Assert.DoesNotContain("answers", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("questions", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("provider", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(foreignInterviewId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        }

        using (var secondPage = await ownerClient.GetAsync("/api/v1/interviews?page=2&pageSize=2"))
        {
            var items = (await DataAsync(secondPage)).GetProperty("items").EnumerateArray().ToArray();
            Assert.Single(items);
            Assert.Equal(firstInterviewId, items[0].GetProperty("id").GetGuid());
        }

        using (var invalidPage = await ownerClient.GetAsync("/api/v1/interviews?page=1&pageSize=101"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
            Assert.Equal("INVALID_PAGINATION", await ErrorCodeAsync(invalidPage));
        }

        var ownerResumeId = await SeedReadyResumeAsync(factory, owner.UserId, "history-owner.pdf", At(1));
        var foreignResumeId = await SeedReadyResumeAsync(factory, other.UserId, "history-other.pdf", At(1));
        var olderAnalysisId = await SeedAnalysisAsync(factory, owner.UserId, ownerResumeId, At(1), "old-role");
        var newerAnalysisId = await SeedAnalysisAsync(factory, owner.UserId, ownerResumeId, At(2), "new-role");
        await SeedAnalysisAsync(factory, other.UserId, foreignResumeId, At(3), "foreign-role");

        using (var analysisPage = await ownerClient.GetAsync("/api/v1/resume-analyses?page=1&pageSize=1"))
        {
            Assert.Equal(HttpStatusCode.OK, analysisPage.StatusCode);
            var data = await DataAsync(analysisPage);
            var item = Assert.Single(data.GetProperty("items").EnumerateArray());
            Assert.Equal(newerAnalysisId, item.GetProperty("id").GetGuid());
            Assert.Equal(2, data.GetProperty("totalCount").GetInt32());
            Assert.Equal("new-role", item.GetProperty("context").GetProperty("targetRole").GetString());
            var body = await analysisPage.Content.ReadAsStringAsync();
            Assert.DoesNotContain("result", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("contextJson", body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.NotEqual(firstJdId, secondJdId);
        Assert.NotEqual(olderAnalysisId, newerAnalysisId);
    }

    [Fact]
    public async Task PracticeAgainCreatesANewFocusedSessionAndSupportsDistinctIdempotencyKeys()
    {
        var aiProvider = new TestAiProvider();
        using var factory = new NexoraApiFactory(aiProvider);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "Practice again candidate");
        Authorize(client, account);
        await SeedEntitlementAsync(factory, account.UserId, 3);

        var sourceInterviewId = await StartInterviewIdAsync(client, "practice-again-source");
        await ProcessJobsAsync(factory);
        var active = await GetInterviewAsync(client, sourceInterviewId);
        var firstQuestionId = active.GetProperty("questions")[0].GetProperty("id").GetGuid();
        var secondAnswer = await AnswerAsync(client, sourceInterviewId, firstQuestionId, "Tôi giới thiệu kinh nghiệm.", "practice-again-answer-one");
        var secondQuestionId = secondAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        var thirdAnswer = await AnswerAsync(client, sourceInterviewId, secondQuestionId, "Tôi đã xử lý một tình huống khó.", "practice-again-answer-two");
        var thirdQuestionId = thirdAnswer.GetProperty("nextQuestion").GetProperty("id").GetGuid();
        await AnswerAsync(client, sourceInterviewId, thirdQuestionId, "Tôi phù hợp với vai trò này.", "practice-again-answer-three");
        await CompleteAsync(client, sourceInterviewId, "practice-again-complete");
        await ProcessJobsAsync(factory);

        using var sourceReportBefore = await client.GetAsync($"/api/v1/interviews/{sourceInterviewId}/report");
        Assert.Equal(HttpStatusCode.OK, sourceReportBefore.StatusCode);
        var sourceReport = await DataAsync(sourceReportBefore);
        var sourceReportId = sourceReport.GetProperty("id").GetGuid();
        var sourceScore = sourceReport.GetProperty("overallScore").GetInt32();

        using var practiceAgain = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{sourceInterviewId}/practice-again")
        {
            Content = JsonContent.Create(new
            {
                questionId = secondQuestionId,
                focus = InterviewQuestionValues.BehavioralStar
            })
        };
        practiceAgain.Headers.Add("Idempotency-Key", "practice-again-repeat-question");
        using var practiceAgainResponse = await client.SendAsync(practiceAgain);
        Assert.Equal(HttpStatusCode.Created, practiceAgainResponse.StatusCode);
        var repeated = await DataAsync(practiceAgainResponse);
        var newInterviewId = repeated.GetProperty("id").GetGuid();
        Assert.NotEqual(sourceInterviewId, newInterviewId);
        Assert.Equal(PracticeValues.Starting, repeated.GetProperty("status").GetString());

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var session = await db.InterviewSessions.AsNoTracking().SingleAsync(item => item.Id == newInterviewId);
            Assert.Equal(account.UserId, session.UserId);
            Assert.Equal(sourceInterviewId, session.SourceInterviewId);
            Assert.Equal(secondQuestionId, session.SourceQuestionId);
            Assert.Equal(InterviewPracticeValues.RepeatQuestion, session.PracticeReason);
            Assert.Equal(InterviewQuestionValues.BehavioralStar, session.FocusTopic);
            Assert.Equal(2, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Reserve));
        }

        using var secondPracticeAgain = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{sourceInterviewId}/practice-again")
        {
            Content = JsonContent.Create(new
            {
                questionId = secondQuestionId,
                focus = InterviewQuestionValues.BehavioralStar
            })
        };
        secondPracticeAgain.Headers.Add("Idempotency-Key", "practice-again-repeat-question-second");
        using var secondPracticeAgainResponse = await client.SendAsync(secondPracticeAgain);
        Assert.Equal(HttpStatusCode.Created, secondPracticeAgainResponse.StatusCode);
        var secondNewInterviewId = (await DataAsync(secondPracticeAgainResponse)).GetProperty("id").GetGuid();
        Assert.NotEqual(newInterviewId, secondNewInterviewId);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var secondSession = await db.InterviewSessions.AsNoTracking().SingleAsync(item => item.Id == secondNewInterviewId);
            Assert.Equal(account.UserId, secondSession.UserId);
            Assert.Equal(sourceInterviewId, secondSession.SourceInterviewId);
            Assert.Equal(secondQuestionId, secondSession.SourceQuestionId);
            Assert.Equal(InterviewPracticeValues.RepeatQuestion, secondSession.PracticeReason);
            Assert.Equal(InterviewQuestionValues.BehavioralStar, secondSession.FocusTopic);

            var practiceReservationKeys = await db.UsageEvents.AsNoTracking()
                .Where(item => item.UserId == account.UserId && item.Action == BillingValues.Reserve &&
                    (item.SourceId == newInterviewId.ToString("N") || item.SourceId == secondNewInterviewId.ToString("N")))
                .Select(item => item.IdempotencyKey)
                .ToArrayAsync();
            Assert.Equal(2, practiceReservationKeys.Length);
            Assert.Contains("interview-practice:practice-again-repeat-question", practiceReservationKeys);
            Assert.Contains("interview-practice:practice-again-repeat-question-second", practiceReservationKeys);
            Assert.Equal(3, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Reserve));
        }

        using var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{sourceInterviewId}/practice-again")
        {
            Content = JsonContent.Create(new
            {
                questionId = secondQuestionId,
                focus = InterviewQuestionValues.BehavioralStar
            })
        };
        replay.Headers.Add("Idempotency-Key", "practice-again-repeat-question");
        using var replayResponse = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        Assert.Equal(newInterviewId, (await DataAsync(replayResponse)).GetProperty("id").GetGuid());

        using var secondReplay = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{sourceInterviewId}/practice-again")
        {
            Content = JsonContent.Create(new
            {
                questionId = secondQuestionId,
                focus = InterviewQuestionValues.BehavioralStar
            })
        };
        secondReplay.Headers.Add("Idempotency-Key", "practice-again-repeat-question-second");
        using var secondReplayResponse = await client.SendAsync(secondReplay);
        Assert.Equal(HttpStatusCode.Created, secondReplayResponse.StatusCode);
        Assert.Equal(secondNewInterviewId, (await DataAsync(secondReplayResponse)).GetProperty("id").GetGuid());

        using var conflict = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{sourceInterviewId}/practice-again")
        {
            Content = JsonContent.Create(new
            {
                questionId = firstQuestionId,
                focus = InterviewQuestionValues.SelfIntroduction
            })
        };
        conflict.Headers.Add("Idempotency-Key", "practice-again-repeat-question");
        using var conflictResponse = await client.SendAsync(conflict);
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);
        Assert.Equal("IDEMPOTENCY_CONFLICT", await ErrorCodeAsync(conflictResponse));

        using var sourceReportAfter = await client.GetAsync($"/api/v1/interviews/{sourceInterviewId}/report");
        var sourceReportAfterData = await DataAsync(sourceReportAfter);
        Assert.Equal(sourceReportId, sourceReportAfterData.GetProperty("id").GetGuid());
        Assert.Equal(sourceScore, sourceReportAfterData.GetProperty("overallScore").GetInt32());
        Assert.Equal(3, await CountSessionsAsync(factory, account.UserId));
    }

    [Fact]
    public async Task PracticeAgainInvalidReasonReturnsReadableVietnameseMessage()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var account = await RegisterAsync(client, "Practice validation candidate");
        Authorize(client, account);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/interviews/{Guid.NewGuid()}/practice-again")
        {
            Content = JsonContent.Create(new { reason = "unsupported" })
        };
        request.Headers.Add("Idempotency-Key", "practice-again-invalid-reason");
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("PRACTICE_REASON_INVALID", await ErrorCodeAsync(response));
        var message = await ErrorMessageAsync(response);
        Assert.Equal("Nguồn luyện tập không hợp lệ.", message);
        Assert.DoesNotContain("Ã", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Â", message, StringComparison.Ordinal);
        Assert.DoesNotContain("á»", message, StringComparison.Ordinal);
    }

    private static void Authorize(HttpClient client, Account account) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

    private static async Task<Account> RegisterAsync(HttpClient client, string displayName)
    {
        var email = $"practice-loop-{Guid.NewGuid():N}@example.test";
        using var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName
        });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var data = await DataAsync(login);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<Guid> CreateJobDescriptionAsync(HttpClient client, string title, string content)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/job-descriptions", new { title, content });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateCareerGoalAsync(HttpClient client, string targetRole, string seniority, Guid? jobDescriptionId = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/career-goals", new
        {
            targetRole,
            seniority,
            targetJobDescriptionId = jobDescriptionId
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<HttpResponseMessage> StartAsync(HttpClient client, object body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/interviews")
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    private static async Task<Guid> StartInterviewIdAsync(HttpClient client, string key)
    {
        using var response = await StartAsync(client, new
        {
            role = "Backend Engineer",
            seniority = "junior",
            interviewType = "behavioral",
            difficulty = "medium"
        }, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await DataAsync(response)).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetInterviewAsync(HttpClient client, Guid interviewId)
    {
        using var response = await client.GetAsync($"/api/v1/interviews/{interviewId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await DataAsync(response);
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

    private static async Task<Guid> SeedReadyResumeAsync(
        NexoraApiFactory factory,
        Guid userId,
        string fileName,
        DateTimeOffset createdAt)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var file = new StoredFile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StorageKey = $"practice-loop/{Guid.NewGuid():N}",
            FileName = fileName,
            ContentType = "application/pdf",
            Size = 100,
            Checksum = Guid.NewGuid().ToString("N"),
            CreatedAt = createdAt
        };
        var resume = new ResumeRecord
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            StoredFileId = file.Id,
            Status = PracticeValues.Ready,
            ExtractedText = "Ready resume evidence.",
            Version = 1,
            CreatedAt = createdAt,
            UpdatedAt = createdAt
        };
        db.AddRange(file, resume);
        await db.SaveChangesAsync();
        return resume.Id;
    }

    private static async Task<Guid> SeedAnalysisAsync(
        NexoraApiFactory factory,
        Guid userId,
        Guid resumeId,
        DateTimeOffset createdAt,
        string targetRole)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var analysis = new ResumeAnalysis
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ResumeId = resumeId,
            Mode = ResumeAnalysisModes.FieldBenchmark,
            ContextJson = JsonSerializer.Serialize(new
            {
                mode = ResumeAnalysisModes.FieldBenchmark,
                industry = "technology",
                targetRole,
                seniority = "senior"
            }, JsonOptions),
            Status = PracticeValues.Completed,
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            CompletedAt = createdAt
        };
        db.ResumeAnalyses.Add(analysis);
        await db.SaveChangesAsync();
        return analysis.Id;
    }

    private static async Task SetJobDescriptionDatesAsync(NexoraApiFactory factory, Guid id, DateTimeOffset createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var item = await db.JobDescriptions.SingleAsync(description => description.Id == id);
        item.CreatedAt = createdAt;
        item.UpdatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    private static async Task SetInterviewDatesAsync(NexoraApiFactory factory, Guid id, DateTimeOffset createdAt)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var item = await db.InterviewSessions.SingleAsync(session => session.Id == id);
        item.CreatedAt = createdAt;
        item.UpdatedAt = createdAt;
        await db.SaveChangesAsync();
    }

    private static async Task<int> CountSessionsAsync(NexoraApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().InterviewSessions.CountAsync(item => item.UserId == userId);
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, string path, object body) =>
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = JsonContent.Create(body)
        });

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task<string?> ErrorMessageAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("error").GetProperty("message").GetString();
    }

    private static DateTimeOffset At(int day) => new(2026, 9, day, 0, 0, 0, TimeSpan.Zero);

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

    private sealed record Account(Guid UserId, string AccessToken);
}
