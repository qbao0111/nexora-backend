using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Authorization;
using Nexora.Business.ContentReports;
using Nexora.Business.Practice;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class ContentReportsPostgresApiTests
{
    [PostgresFact]
    public async Task PostgreSqlEnforcesOwnedContentAndSerializesCompetingModerationClaims()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        await using var factory = NexoraApiFactory.CreatePostgres(connectionString);
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var reporter = await RegisterAsync(client, "content-report-pg-owner@example.test");
        var secondUser = await RegisterAsync(client, "content-report-pg-other@example.test");
        var ownId = Guid.NewGuid();
        var foreignId = Guid.NewGuid();
        await SeedAttemptAsync(factory, reporter.UserId, ownId);
        await SeedAttemptAsync(factory, secondUser.UserId, foreignId);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", reporter.AccessToken);
        using var create = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId = ownId,
            reasonCode = ContentReportValues.Inaccurate
        });
        Assert.Equal(HttpStatusCode.Accepted, create.StatusCode);
        var receipt = await DataAsync(create);
        var reportId = receipt.GetProperty("reportId").GetGuid();
        var receivedAt = receipt.GetProperty("receivedAt").GetDateTimeOffset();

        using var foreign = await client.PostAsJsonAsync("/api/v1/content-reports", new
        {
            contentType = ContentReportValues.StarEvaluation,
            contentId = foreignId,
            reasonCode = ContentReportValues.Inaccurate
        });
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);

        var firstAdminToken = await MakeAdminAsync(factory, reporter.UserId, "content-report-pg-owner@example.test");
        var secondAdminToken = await MakeAdminAsync(factory, secondUser.UserId, "content-report-pg-other@example.test");
        using var firstAdmin = factory.CreateHttpsClient();
        using var secondAdmin = factory.CreateHttpsClient();
        firstAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", firstAdminToken);
        secondAdmin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondAdminToken);
        var firstReviewTask = firstAdmin.PostAsync($"/api/v1/admin/content-reports/{reportId}/review", null);
        var secondReviewTask = secondAdmin.PostAsync($"/api/v1/admin/content-reports/{reportId}/review", null);
        using var firstReview = await firstReviewTask;
        using var secondReview = await secondReviewTask;
        var codes = new[] { firstReview.StatusCode, secondReview.StatusCode };
        Assert.Single(codes, code => code == HttpStatusCode.OK);
        Assert.Single(codes, code => code == HttpStatusCode.Conflict);

        var from = Uri.EscapeDataString(receivedAt.AddSeconds(-1).ToString("O"));
        var to = Uri.EscapeDataString(receivedAt.AddSeconds(1).ToString("O"));
        using var queue = await firstAdmin.GetAsync(
            $"/api/v1/admin/content-reports?status=reviewing&contentType=star_evaluation&from={from}&to={to}");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        var page = await DataAsync(queue);
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
        var entry = page.GetProperty("items")[0];
        Assert.Equal(ContentReportValues.Reviewing, entry.GetProperty("status").GetString());
        Assert.Contains(entry.GetProperty("moderatorUserId").GetGuid(), new[] { reporter.UserId, secondUser.UserId });
    }

    private static async Task SeedAttemptAsync(NexoraApiFactory factory, Guid userId, Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        db.StarAttempts.Add(new StarAttempt
        {
            Id = id,
            UserId = userId,
            Question = "Private question",
            Answer = "Private answer",
            Status = PracticeFeatureValues.Completed,
            EvaluationJson = "{\"feedback\":\"AI feedback\"}",
            ModelVersion = "test-model",
            PromptVersion = "test-prompt",
            SchemaVersion = "test-schema",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Account> RegisterAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email,
            password = "Strong!Pass123",
            displayName = "Candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        var data = await DataAsync(login);
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<string> MakeAdminAsync(NexoraApiFactory factory, Guid userId, string email)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (!await roleManager.RoleExistsAsync(RoleNames.Admin))
                await roleManager.CreateAsync(new IdentityRole<Guid>(RoleNames.Admin));
            var user = await userManager.FindByIdAsync(userId.ToString()) ?? throw new InvalidOperationException("Test user missing.");
            await userManager.AddToRoleAsync(user, RoleNames.Admin);
        }
        using var client = factory.CreateHttpsClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await DataAsync(login)).GetProperty("accessToken").GetString()!;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    private sealed record Account(Guid UserId, string AccessToken);
}
