using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Ai;
using Nexora.Business.Common;
using Nexora.Business.Practice;
using Nexora.Data.Persistence;
using Nexora.Data.Practice;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class ProviderAdmissionTests
{
    [Fact]
    public async Task SpeechIssuanceCannotOpenThirdDistinctInterviewWithinTokenLifetime()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        var gate = factory.Services.GetRequiredService<IProviderAdmission>();
        var user = Guid.NewGuid();
        for (var index = 0; index < 2; index++)
        {
            var ticket = await gate.ReserveAsync(new ProviderAdmissionRequest(user, Guid.NewGuid(),
                "speech.token", Guid.NewGuid().ToString("N"), 1, 1), CancellationToken.None);
            await gate.CompleteAsync(ticket, null, CancellationToken.None);
        }
        await Assert.ThrowsAsync<BusinessException>(() => gate.ReserveAsync(new ProviderAdmissionRequest(user,
            Guid.NewGuid(), "speech.token", "third-interview", 1, 1), CancellationToken.None));
    }
    [Fact]
    public async Task ChangingCorrelationDoesNotReauthorizeSameLogicalProviderAttempt()
    {
        var provider = new TestAiProvider();
        using var factory = new NexoraApiFactory(provider);
        factory.InitializeDatabase();
        var executor = factory.Services.GetRequiredService<IStructuredAiExecutor>();
        var context = new AiOperationContext("first-correlation", Guid.NewGuid(), JobId: Guid.NewGuid());
        await executor.ExecuteAsync(AiOperations.InterviewFirstQuestion, "question-topic: technical", context, CancellationToken.None);
        await Assert.ThrowsAsync<BusinessException>(() => executor.ExecuteAsync(AiOperations.InterviewFirstQuestion,
            "question-topic: technical", context with { CorrelationId = "another-correlation" }, CancellationToken.None));
        Assert.Equal(1, provider.TotalCalls);
    }
    [PostgresFact]
    public async Task AdditiveMigrationUpgradesPreviousSchemaWithoutChangingExistingRows()
    {
        using var factory = NexoraApiFactory.CreatePostgres(Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!);
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var baselineCount = await db.FeatureDefinitions.CountAsync();
        Assert.True(baselineCount > 0);
        var migrations = db.Database.GetMigrations().ToArray();
        Assert.EndsWith("_AddProviderCallReservations", migrations[^1]);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[^2]);
        Assert.Equal(baselineCount, await db.FeatureDefinitions.CountAsync());
        await migrator.MigrateAsync();
        Assert.Equal(baselineCount, await db.FeatureDefinitions.CountAsync());
        Assert.Equal(0, await db.ProviderCallReservations.CountAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [PostgresFact]
    public async Task PostgresConcurrentAdmissionAndRestartCannotExceedDurableLimits()
    {
        using var factory = NexoraApiFactory.CreatePostgres(Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!);
        factory.InitializeDatabase();
        var scopes = factory.Services.GetRequiredService<IServiceScopeFactory>();
        var options = Options.Create(new ProviderBudgetOptions { GlobalInFlight = 1, GlobalDailyCalls = 1 });
        var gate = new ProviderAdmission(scopes, options, TimeProvider.System, NullLogger<ProviderAdmission>.Instance);
        var requests = Enumerable.Range(0, 10).Select(_ => new ProviderAdmissionRequest(Guid.NewGuid(),
            Guid.NewGuid(), "interview.evaluate", "answer", 1, 1_000)).ToArray();
        var outcomes = await Task.WhenAll(requests.Select(async request =>
        {
            try { return await gate.ReserveAsync(request, CancellationToken.None); }
            catch (BusinessException) { return Guid.Empty; }
        }));
        var ticket = Assert.Single(outcomes, id => id != Guid.Empty);
        await gate.CompleteAsync(ticket, null, CancellationToken.None);
        var restarted = new ProviderAdmission(scopes, options, TimeProvider.System, NullLogger<ProviderAdmission>.Instance);
        await Assert.ThrowsAsync<BusinessException>(() => restarted.ReserveAsync(
            new ProviderAdmissionRequest(Guid.NewGuid(), Guid.NewGuid(), "interview.evaluate", "new", 1, 1_000), CancellationToken.None));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.ProviderCallReservations.CountAsync());
    }

    [Fact]
    public async Task TimeoutRetainsChargeStartsCooldownAndCompletionCannotEraseFailure()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?> { ["Ai:Budget:CooldownFailureThreshold"] = "1" });
        factory.InitializeDatabase();
        var gate = factory.Services.GetRequiredService<IProviderAdmission>();
        var request = new ProviderAdmissionRequest(Guid.NewGuid(), Guid.NewGuid(), "interview.evaluate", "answer", 1, 1_000);
        var ticket = await gate.ReserveAsync(request, CancellationToken.None);
        await gate.CompleteAsync(ticket, null, CancellationToken.None, AiProviderFailureKind.Timeout);
        await gate.CompleteAsync(ticket, new AiTokenUsage(0, 0), CancellationToken.None);
        await Assert.ThrowsAsync<BusinessException>(() => gate.ReserveAsync(request with { JobId = Guid.NewGuid() }, CancellationToken.None));
        using var scope = factory.Services.CreateScope();
        var record = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().ProviderCallReservations.SingleAsync();
        Assert.Equal(1_000, record.ReservedTokens);
        Assert.Equal("Timeout", record.FailureKind);
        Assert.NotNull(record.CooldownUntil);
        Assert.Null(record.ActualPromptTokens);
    }

    [Fact]
    public async Task QueueFullRejectsWithinTransactionAndLeavesExistingWorkUnchanged()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?> { ["Ai:Budget:MaximumQueuedJobs"] = "1" });
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        db.OutboxEvents.Add(new Nexora.Data.Billing.OutboxEvent { Id = Guid.NewGuid(), Type = "InterviewStartRequested", Status = "pending", Payload = "{}", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            db.OutboxEvents.Add(new Nexora.Data.Billing.OutboxEvent { Id = Guid.NewGuid(), Type = "InterviewStartRequested", Status = "pending", Payload = "{}", CreatedAt = DateTimeOffset.UtcNow });
            var error = await Assert.ThrowsAsync<BusinessException>(() => scope.ServiceProvider.GetRequiredService<PaidJobQueueAdmission>().CheckAsync(CancellationToken.None));
            Assert.Equal("AI_QUEUE_FULL", error.Code);
            Assert.Equal(BusinessErrorKind.RateLimited, error.Kind);
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OutboxEvents.CountAsync());
    }

    [Fact]
    public async Task ConcurrentCallsCannotExceedDurableBudgetAndDuplicatesNeverRefund()
    {
        using var factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["Ai:Budget:GlobalDailyCalls"] = "2", ["Ai:Budget:GlobalInFlight"] = "1"
        });
        factory.InitializeDatabase();
        var gate = factory.Services.GetRequiredService<IProviderAdmission>();
        var userId = Guid.NewGuid();
        var jobs = Enumerable.Range(0, 10).Select(_ => new ProviderAdmissionRequest(
            userId, Guid.NewGuid(), "interview.evaluate", "logical-answer", 1, 2_000)).ToArray();
        var outcomes = await Task.WhenAll(jobs.Select(async request =>
        {
            try { return await gate.ReserveAsync(request, CancellationToken.None); }
            catch (BusinessException exception) when (exception.Code == "AI_ADMISSION_DENIED") { return Guid.Empty; }
        }));
        var ticket = Assert.Single(outcomes, id => id != Guid.Empty);
        await gate.CompleteAsync(ticket, new AiTokenUsage(100, 200), CancellationToken.None);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var first = await db.ProviderCallReservations.SingleAsync();
        Assert.Equal(2_000, first.ReservedTokens);
        Assert.Equal(100, first.ActualPromptTokens);
        var duplicate = new ProviderAdmissionRequest(userId, first.JobId, first.Purpose, first.OperationKey, first.Attempt, 2_000);
        await Assert.ThrowsAsync<BusinessException>(() => gate.ReserveAsync(duplicate, CancellationToken.None));
        var second = await gate.ReserveAsync(jobs.First(job => job.JobId != first.JobId), CancellationToken.None);
        await gate.CompleteAsync(second, null, CancellationToken.None);
        await Assert.ThrowsAsync<BusinessException>(() => gate.ReserveAsync(
            new ProviderAdmissionRequest(userId, Guid.NewGuid(), first.Purpose, "another-key", 1, 2_000), CancellationToken.None));
        Assert.Equal(2, await db.ProviderCallReservations.CountAsync());
    }
}
