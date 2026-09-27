using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.ContentReports;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Data.Billing;
using Nexora.Data.ContentReports;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;
using Nexora.Data.Realtime;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class PrivacyPracticeDeletionTests
{
    private static readonly bool[] PracticeTypes = [false, true];
    [Fact]
    public async Task DeletionPurgesPrivatePracticeAndPreservesOtherOwnerAndLedgers()
    {
        await using var factory = new NexoraApiFactory();
        await VerifyPurgeAsync(factory);
    }

    [PostgresFact]
    public async Task PostgresDeletionPurgesPrivatePracticeAndPreservesOtherOwnerAndLedgers()
    {
        await using var factory = NexoraApiFactory.CreatePostgres(Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!);
        await VerifyPurgeAsync(factory);
    }

    [PostgresFact]
    public Task ScenarioEvaluationCannotPersistAfterDeletion() => VerifyRaceAsync(false, false);

    [PostgresFact]
    public Task StarEvaluationCannotPersistAfterDeletion() => VerifyRaceAsync(true, false);

    [PostgresFact]
    public Task ScenarioFailureCannotRecreateNotificationAfterDeletion() => VerifyRaceAsync(false, true);

    [PostgresFact]
    public Task StarFailureCannotRecreateNotificationAfterDeletion() => VerifyRaceAsync(true, true);

    [PostgresFact]
    public async Task DeletionRemovesEvaluationsCompletedBeforeThePurge()
    {
        await using var factory = NexoraApiFactory.CreatePostgres(Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!);
        factory.InitializeDatabase();
        var owner = await RegisterAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await SeedAsync(scope.ServiceProvider, owner);
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<IScenarioStarJobProcessor>().ProcessPendingAsync(CancellationToken.None));
        }
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.All(await db.ScenarioAttempts.ToArrayAsync(), item => Assert.NotNull(item.EvaluationJson));
            Assert.All(await db.StarAttempts.ToArrayAsync(), item => Assert.NotNull(item.EvaluationJson));
        }
        await DeleteAsync(factory, owner);
        await AssertPurgedAsync(factory, owner);
    }

    private static async Task VerifyPurgeAsync(NexoraApiFactory factory)
    {
        factory.InitializeDatabase();
        var owner = await RegisterAsync(factory);
        var other = await RegisterAsync(factory);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await SeedAsync(scope.ServiceProvider, owner);
            await SeedAsync(scope.ServiceProvider, other);
        }
        await DeleteAsync(factory, owner);
        await AssertPurgedAsync(factory, owner);
        await using var verification = factory.Services.CreateAsyncScope();
        var db = verification.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(3, await db.ScenarioAttempts.CountAsync(item => item.UserId == other));
        Assert.Equal(3, await db.StarAttempts.CountAsync(item => item.UserId == other));
        Assert.Equal(6, await db.RealtimeNotifications.CountAsync(item => item.UserId == other));
        Assert.Equal(6, await db.IdempotencyRecords.CountAsync(item => item.ActorId == other));
        Assert.Equal(6, await db.ContentReports.CountAsync(item => item.ReporterUserId == other));
        Assert.Equal(6, await db.OutboxEvents.CountAsync());
        Assert.All(await db.ScenarioAttempts.ToArrayAsync(), item => Assert.Equal($"private-{other:N}", item.Answer));
        Assert.All(await db.StarAttempts.ToArrayAsync(), item =>
        {
            Assert.Equal($"private-{other:N}", item.Question);
            Assert.Equal($"private-{other:N}", item.Answer);
        });
        Assert.All(await db.ContentReports.ToArrayAsync(), item =>
        {
            using var snapshot = JsonDocument.Parse(item.ContentSnapshot!);
            Assert.Equal($"private-{other:N}", snapshot.RootElement.GetProperty("privateContent").GetString());
        });
        Assert.Equal(1, await db.Scenarios.CountAsync());
        Assert.All(await db.OutboxEvents.Select(item => item.Payload).ToArrayAsync(), payload => Assert.Contains(other.ToString("N"), payload));
        foreach (var userId in new[] { owner, other })
        {
            var ledger = await db.FeatureUsageEvents.Where(item => item.UserId == userId).ToArrayAsync();
            Assert.Equal(6, ledger.Count(item => item.Action == FeatureValues.Reserve));
            Assert.Equal(2, ledger.Count(item => item.Action == FeatureValues.Consume));
            Assert.Equal(userId == owner ? 4 : 2, ledger.Count(item => item.Action == FeatureValues.Void));
            var features = await db.EntitlementFeatures.Include(item => item.Entitlement)
                .Where(item => item.Entitlement.UserId == userId &&
                    (item.FeatureCode == FeatureValues.Scenario || item.FeatureCode == FeatureValues.StarBuilder)).ToArrayAsync();
            Assert.All(features, item => Assert.Equal(userId == owner ? 0 : 1, item.Reserved));
            Assert.All(features, item => Assert.Equal(1, item.Consumed));
        }
        Assert.NotNull((await db.Users.SingleAsync(item => item.Id == owner)).DeletedAt);
        Assert.Null((await db.Users.SingleAsync(item => item.Id == other)).DeletedAt);
    }

    private static async Task VerifyRaceAsync(bool star, bool fail)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new TestAiProvider();
        var purpose = star ? "star.evaluate" : "scenario.evaluate";
        provider.EnqueueAsyncHandler(purpose, async (request, cancellationToken) =>
        {
            entered.TrySetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            if (fail) throw new InvalidOperationException("Controlled provider failure.");
            var fallback = new TestAiProvider();
            return star
                ? (object)await fallback.GenerateStructuredAsync<StarEvaluation>(request, cancellationToken)
                : await fallback.GenerateStructuredAsync<ScenarioEvaluationResult>(request, cancellationToken);
        });
        if (fail) provider.EnqueueResponse(purpose, new InvalidOperationException("Controlled provider failure."));
        await using var factory = NexoraApiFactory.CreatePostgres(Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!, provider);
        factory.InitializeDatabase();
        var owner = await RegisterAsync(factory);
        await using (var seed = factory.Services.CreateAsyncScope())
            await SeedAsync(seed.ServiceProvider, owner, star);
        await using var worker = factory.Services.CreateAsyncScope();
        var work = worker.ServiceProvider.GetRequiredService<IScenarioStarJobProcessor>().ProcessPendingAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await DeleteAsync(factory, owner);
            await AssertPurgedAsync(factory, owner);
        }
        finally
        {
            resume.TrySetResult();
        }
        await work.WaitAsync(TimeSpan.FromSeconds(30));
        await AssertPurgedAsync(factory, owner);
    }

    private static async Task SeedAsync(IServiceProvider services, Guid userId, bool? onlyStar = null)
    {
        var db = services.GetRequiredService<NexoraDbContext>();
        var entitlement = await db.Entitlements.SingleAsync(item => item.UserId == userId);
        var features = await db.EntitlementFeatures.Where(item => item.EntitlementId == entitlement.Id).ToArrayAsync();
        var definitions = await db.FeatureDefinitions.Where(item => item.Code == FeatureValues.StarBuilder || item.Code == FeatureValues.Scenario).ToArrayAsync();
        foreach (var definition in definitions.Where(item => features.All(feature => feature.FeatureCode != item.Code)))
            db.EntitlementFeatures.Add(new EntitlementFeature
            {
                Id = Guid.NewGuid(), EntitlementId = entitlement.Id, FeatureDefinitionId = definition.Id, FeatureCode = definition.Code,
                IsEnabled = true, Limit = 10, ConcurrencyToken = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
        foreach (var feature in features.Where(item => item.FeatureCode == FeatureValues.StarBuilder || item.FeatureCode == FeatureValues.Scenario))
        {
            feature.IsEnabled = true;
            feature.Limit = 10;
        }
        await db.SaveChangesAsync();
        var scenarioId = await db.Scenarios.Select(item => (Guid?)item.Id).FirstOrDefaultAsync();
        if (scenarioId is null)
        {
            var now = DateTimeOffset.UtcNow;
            var category = new ScenarioCategory
            {
                Id = Guid.NewGuid(), Slug = "privacy-test", Name = "Privacy test", IsActive = true, CreatedAt = now, UpdatedAt = now
            };
            var scenario = new Scenario
            {
                Id = Guid.NewGuid(), Category = category, CategoryId = category.Id, Slug = "privacy-test", Title = "Global practice",
                Summary = "Global scenario", Content = "Analyze a service failure.", Difficulty = "easy", Competency = "problem_analysis",
                Status = "published", CreatedAt = now, UpdatedAt = now
            };
            db.Scenarios.Add(scenario);
            await db.SaveChangesAsync();
            scenarioId = scenario.Id;
        }
        var billing = services.GetRequiredService<IFeatureEntitlementService>();
        foreach (var star in PracticeTypes.Where(star => onlyStar is null || onlyStar == star))
        {
            for (var index = 0; index < (onlyStar is null ? 3 : 1); index++)
            {
                var id = Guid.NewGuid();
                var code = star ? FeatureValues.StarBuilder : FeatureValues.Scenario;
                var reservation = await billing.ReserveAsync(userId, code, id.ToString("N"), id.ToString("N"), CancellationToken.None);
                if (index == 1) await billing.ConsumeAsync(userId, reservation.EventId, CancellationToken.None);
                if (index == 2) await billing.VoidAsync(userId, reservation.EventId, CancellationToken.None);
                var now = DateTimeOffset.UtcNow;
                var marker = $"private-{userId:N}";
                var privateJson = JsonSerializer.Serialize(new { privateContent = marker });
                if (star)
                    db.StarAttempts.Add(new StarAttempt
                    {
                        Id = id, UserId = userId, Question = marker, Answer = marker, EvaluationJson = index == 0 ? null : $"{{\"private\":\"{marker}\"}}",
                        Status = index == 0 ? PracticeFeatureValues.Queued : PracticeFeatureValues.Completed,
                        UsageReservationId = reservation.EventId, CreatedAt = now, UpdatedAt = now
                    });
                else
                    db.ScenarioAttempts.Add(new ScenarioAttempt
                    {
                        Id = id, UserId = userId, ScenarioId = scenarioId.Value, Answer = marker, EvaluationJson = index == 0 ? null : $"{{\"private\":\"{marker}\"}}",
                        Status = index == 0 ? PracticeFeatureValues.Queued : PracticeFeatureValues.Completed,
                        UsageReservationId = reservation.EventId, CreatedAt = now, UpdatedAt = now
                    });
                db.OutboxEvents.Add(new OutboxEvent
                {
                    Id = Guid.NewGuid(), AggregateId = id, AggregateType = star ? "star_attempt" : "scenario_attempt",
                    Type = star ? PracticeFeatureValues.StarEvaluationJob : PracticeFeatureValues.ScenarioEvaluationJob,
                    Status = index == 0 ? BillingValues.Pending : BillingValues.Processed, Payload = privateJson, CreatedAt = now
                });
                db.RealtimeNotifications.Add(new RealtimeNotification
                {
                    UserId = userId, ResourceId = id, ResourceType = star ? "starAttempt" : "scenarioAttempt", Status = "completed", CreatedAt = now
                });
                db.IdempotencyRecords.Add(new IdempotencyRecord
                {
                    Id = Guid.NewGuid(), ActorId = userId, Operation = star ? "star-attempt.create" : "scenario-attempt.submit",
                    Key = id.ToString("N"), RequestFingerprint = marker, ResourceId = id, CreatedAt = now
                });
                db.ContentReports.Add(new ContentReport
                {
                    Id = Guid.NewGuid(), ReporterUserId = userId, ContentId = id,
                    ContentType = star ? ContentReportValues.StarEvaluation : ContentReportValues.ScenarioEvaluation,
                    ReasonCode = ContentReportValues.Inaccurate, Description = marker, ContentSnapshot = privateJson,
                    Status = ContentReportValues.Pending, CreatedAt = now
                });
                await db.SaveChangesAsync();
            }
        }
    }

    private static async Task DeleteAsync(NexoraApiFactory factory, Guid owner)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IPrivacyService>().RequestDeletionAsync(owner, "practice-delete", CancellationToken.None);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>().ProcessPendingAsync(CancellationToken.None));
    }

    private static async Task AssertPurgedAsync(NexoraApiFactory factory, Guid owner)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(PrivacyValues.Completed, (await db.DataPrivacyRequests.SingleAsync(item => item.UserId == owner)).Status);
        Assert.Empty(await db.ScenarioAttempts.Where(item => item.UserId == owner).ToArrayAsync());
        Assert.Empty(await db.StarAttempts.Where(item => item.UserId == owner).ToArrayAsync());
        Assert.Empty(await db.RealtimeNotifications.Where(item => item.UserId == owner).ToArrayAsync());
        Assert.Empty(await db.IdempotencyRecords.Where(item => item.ActorId == owner).ToArrayAsync());
        Assert.Empty(await db.ContentReports.Where(item => item.ReporterUserId == owner).ToArrayAsync());
        Assert.DoesNotContain(await db.OutboxEvents.Select(item => item.Payload).ToArrayAsync(), value => value.Contains(owner.ToString("N"), StringComparison.Ordinal));
    }

    private static async Task<Guid> RegisterAsync(NexoraApiFactory factory)
    {
        using var client = factory.CreateHttpsClient();
        var email = $"practice-delete-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123", displayName = "Private practice" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Users.SingleAsync(item => item.Email == email)).Id;
    }
}
