using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Billing;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class FoundationApiTests : IClassFixture<NexoraApiFactory>
{
    private readonly NexoraApiFactory _factory;

    public FoundationApiTests(NexoraApiFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    [Fact]
    public async Task GuestProtectedMutationReturnsCanonical401T01()
    {
        using var client = _factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/me/profile")
        {
            Content = JsonContent.Create(new { displayName = "Unauthorized" })
        };
        request.Headers.Add("X-Request-Id", "t01-guest-mutation");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("t01-guest-mutation", response.Headers.GetValues("X-Request-Id").Single());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("UNAUTHENTICATED", error.GetProperty("code").GetString());
        Assert.Equal("t01-guest-mutation", error.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task ValidationErrorUsesCanonicalEnvelopeAndSafeRequestId()
    {
        using var client = _factory.CreateHttpsClient();
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email = "invalid", password = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = json.RootElement.GetProperty("error");
        Assert.Equal("VALIDATION_ERROR", error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("requestId").GetString()));
    }

    [Fact]
    public async Task LivenessReadinessAndOpenApiAreAvailable()
    {
        using var client = _factory.CreateHttpsClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Fact]
    public async Task ConfiguredFrontendOriginReceivesCredentialedCorsPreflight()
    {
        await using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["Frontend:AllowedOrigins:0"] = "http://localhost:5173"
        });
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/refresh");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type,idempotency-key");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", response.Headers.GetValues("Access-Control-Allow-Credentials").Single());
        Assert.Contains("POST", response.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("idempotency-key", response.Headers.GetValues("Access-Control-Allow-Headers").Single(), StringComparison.OrdinalIgnoreCase);

        using var rejectedRequest = new HttpRequestMessage(HttpMethod.Options, "/api/v1/auth/refresh");
        rejectedRequest.Headers.Add("Origin", "https://untrusted.example");
        rejectedRequest.Headers.Add("Access-Control-Request-Method", "POST");
        using var rejectedResponse = await client.SendAsync(rejectedRequest);
        Assert.False(rejectedResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task OperationalHealthDegradesWhenJobQueueExceedsThresholdNfrObs01()
    {
        using var client = _factory.CreateHttpsClient();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        db.OutboxEvents.Add(new OutboxEvent
        {
            Id = Guid.NewGuid(),
            Type = "ResumeExtractionRequested",
            AggregateType = "test",
            AggregateId = Guid.NewGuid(),
            Payload = "{}",
            Status = BillingValues.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddHours(-1)
        });
        await db.SaveChangesAsync();

        using var response = await client.GetAsync("/api/v1/health/operations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void PostgresMigrationsAreDiscoverable()
    {
        var options = new DbContextOptionsBuilder<NexoraDbContext>()
            .UseNpgsql("Host=localhost;Database=metadata_only;Username=nexora").Options;
        using var context = new NexoraDbContext(options);
        var migrations = context.Database.GetMigrations().ToArray();
        Assert.Contains(migrations, migration => migration.EndsWith("_InitialIdentityFoundation", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("_Phase2BillingEntitlement", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("_Phase2PlanCatalogue", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("_Phase3CoreAiPractice", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("_Phase4PrivacyHardening", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("_ProductionUploadIntents", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("_CvAnalysisV2", StringComparison.Ordinal));
        Assert.Contains(migrations, migration => migration.EndsWith("_InterviewQuestionContractV1", StringComparison.Ordinal));
        Assert.Contains("20260910162014_B9CareerGoal", migrations);
        Assert.DoesNotContain("20260910072100_B9CareerGoal", migrations);
        Assert.Contains("20260910192103_B11LearningPath", migrations);
    }
}
