using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Privacy;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Data.Privacy;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class RetentionPostgresTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 16, 0, 0, TimeSpan.Zero);

    [PostgresFact]
    public async Task ExpiryBoundariesHoldsAndDryRunAreEnforced()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var active = User();
        var held = User();
        db.Users.AddRange(active, held);
        db.ExternalDeletionVerifications.AddRange(Token(active.Id, Now.AddSeconds(-1)), Token(active.Id, Now),
            Token(active.Id, Now.AddSeconds(1)), Token(held.Id, Now.AddDays(-3)));
        db.RetentionHolds.Add(new RetentionHold { Id = Guid.NewGuid(), UserId = held.Id, ReasonCode = "security", CreatedAt = Now });
        await db.SaveChangesAsync();
        var clock = new RetentionClock(Now);
        var settings = Enabled();
        settings.PurgeEnabled = false;
        var report = await Processor(db, settings, clock).RunDueAsync(CancellationToken.None);
        Assert.True(report.DryRun);
        Assert.Equal(2, report.Eligible);
        Assert.Equal(1, report.Skipped);
        Assert.Equal(0, report.Removed);
        Assert.Equal(4, await db.ExternalDeletionVerifications.CountAsync());
        clock.Advance(TimeSpan.FromHours(6));
        settings.PurgeEnabled = true;
        var purge = await Processor(db, settings, clock).RunDueAsync(CancellationToken.None);
        Assert.Equal(3, purge.Removed);
        Assert.Equal(1, await db.ExternalDeletionVerifications.CountAsync());
        Assert.Null(active.DeletedAt);
        // A global hold protects even expired records, without an email/token identifier.
        db.RetentionHolds.Add(new RetentionHold { Id = Guid.NewGuid(), ReasonCode = "legal", CreatedAt = Now });
        await db.SaveChangesAsync();
        clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(0, (await Processor(db, settings, clock).RunDueAsync(CancellationToken.None)).Removed);
    }

    [PostgresFact]
    public async Task AuditTwelveMonthBoundaryRequiresCompletedDeletionAndPreservesNinetyDayLedgers()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var cutoff = Now.AddMonths(-12);
        var old = User(cutoff);
        var recent = User(cutoff.AddSeconds(1));
        var active = User();
        var held = User(cutoff);
        var incomplete = User(cutoff);
        var ninety = User(Now.AddDays(-90));
        db.Users.AddRange(old, recent, active, held, incomplete, ninety);
        var oldRequest = Request(old.Id, cutoff);
        db.DataPrivacyRequests.AddRange(oldRequest, Request(old.Id, cutoff.AddSeconds(1)), Request(recent.Id, cutoff),
            Request(active.Id, cutoff), Request(held.Id, cutoff), Request(incomplete.Id, cutoff), Request(ninety.Id, Now.AddDays(-90)));
        var failed = Request(incomplete.Id, cutoff);
        failed.Status = PrivacyValues.Failed;
        failed.CompletedAt = null;
        db.DataPrivacyRequests.Add(failed);
        db.RetentionHolds.Add(new RetentionHold { Id = Guid.NewGuid(), UserId = held.Id, ReasonCode = "dispute", CreatedAt = Now });
        var price = await db.PlanPrices.FirstAsync(item => item.AmountMinor > 0);
        var order = new Order
        {
            Id = Guid.NewGuid(), UserId = ninety.Id, PlanPriceId = price.Id, PlanCodeSnapshot = "paid",
            AmountMinor = price.AmountMinor, Currency = price.Currency, Status = BillingValues.Fulfilled,
            PaymentProvider = "fake", ProviderTransactionId = "retention-test", CheckoutUrl = "https://example.test/checkout",
            CreatedAt = Now.AddDays(-100), UpdatedAt = Now.AddDays(-100)
        };
        var subscription = new Subscription { Id = Guid.NewGuid(), UserId = ninety.Id, OrderId = order.Id, Status = "active", StartsAt = Now.AddDays(-100), EndsAt = Now, CreatedAt = Now, UpdatedAt = Now };
        var entitlement = new Entitlement { Id = Guid.NewGuid(), UserId = ninety.Id, SubscriptionId = subscription.Id, PlanCodeSnapshot = "paid", Status = "active", Consumed = 1, StartsAt = Now.AddDays(-100), EndsAt = Now, CreatedAt = Now, UpdatedAt = Now, ConcurrencyToken = Guid.NewGuid() };
        db.Orders.Add(order);
        db.Subscriptions.Add(subscription);
        db.Entitlements.Add(entitlement);
        db.PaymentEvents.Add(new PaymentEvent { Id = Guid.NewGuid(), OrderId = order.Id, Provider = "fake", ProviderEventId = "retention-test", OccurredAt = Now.AddDays(-100), ReceivedAt = Now.AddDays(-100) });
        db.UsageEvents.Add(new UsageEvent { Id = Guid.NewGuid(), UserId = ninety.Id, EntitlementId = entitlement.Id, Action = BillingValues.Consume, Quantity = 1, SourceType = "reservation", SourceId = "test", IdempotencyKey = "retention-test", Reason = "Financially linked usage", CreatedAt = Now.AddDays(-100) });
        var feature = await db.FeatureDefinitions.FirstAsync();
        var featureEntitlement = new EntitlementFeature { Id = Guid.NewGuid(), EntitlementId = entitlement.Id, FeatureDefinitionId = feature.Id, FeatureCode = feature.Code, IsEnabled = true, Consumed = 1, ConcurrencyToken = Guid.NewGuid(), CreatedAt = Now, UpdatedAt = Now };
        db.EntitlementFeatures.Add(featureEntitlement);
        db.FeatureUsageEvents.Add(new FeatureUsageEvent { Id = Guid.NewGuid(), UserId = ninety.Id, EntitlementFeatureId = featureEntitlement.Id, FeatureCode = feature.Code, Action = BillingValues.Consume, Quantity = 1, SourceType = "reservation", SourceId = "test", IdempotencyKey = "retention-test", CreatedAt = Now.AddDays(-100) });
        db.AdminAuditEvents.Add(new AdminAuditEvent { Id = Guid.NewGuid(), AdminUserId = active.Id, Action = "adjustment", TargetType = "user", TargetId = old.Id.ToString(), Reason = "Preserved financial audit", CreatedAt = cutoff });
        db.OutboxEvents.Add(new OutboxEvent { Id = Guid.NewGuid(), Type = "EntitlementGranted", AggregateType = "order", AggregateId = order.Id, Payload = "{}", Status = BillingValues.Processed, CreatedAt = cutoff, ProcessedAt = cutoff });
        await db.SaveChangesAsync();

        var result = await Processor(db, Enabled(), new RetentionClock(Now)).RunDueAsync(CancellationToken.None);
        Assert.Equal(1, result.Removed);
        Assert.Equal(4, result.Skipped);
        Assert.False(await db.DataPrivacyRequests.AnyAsync(item => item.Id == oldRequest.Id));
        Assert.Equal(7, await db.DataPrivacyRequests.CountAsync());
        Assert.Equal(6, await db.Users.CountAsync());
        Assert.Equal(1, await db.Orders.CountAsync());
        Assert.Equal(1, await db.PaymentEvents.CountAsync());
        Assert.Equal(1, await db.Subscriptions.CountAsync());
        Assert.Equal(1, await db.Entitlements.CountAsync());
        Assert.Equal(1, await db.UsageEvents.CountAsync());
        Assert.Equal(1, await db.FeatureUsageEvents.CountAsync());
        Assert.Equal(1, await db.AdminAuditEvents.CountAsync());
        Assert.Equal(1, await db.OutboxEvents.CountAsync());
        Assert.Equal(1, (await db.Entitlements.SingleAsync()).Consumed);
        Assert.Equal(1, (await db.EntitlementFeatures.SingleAsync()).Consumed);
    }

    [PostgresFact]
    public async Task TransactionLockAndDurableSchedulePreventConcurrentOrRestartedSweeps()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var setup = factory.Services.CreateScope();
        var db = setup.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var user = User();
        db.Users.Add(user);
        db.ExternalDeletionVerifications.Add(Token(user.Id, Now.AddDays(-1)));
        await db.SaveChangesAsync();
        await using (var lockTransaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(761604031)");
            Assert.Equal("locked", (await RunNewScopeAsync(factory, new RetentionClock(Now))).Status);
            await lockTransaction.CommitAsync();
        }
        var concurrent = await Task.WhenAll(RunNewScopeAsync(factory, new RetentionClock(Now)), RunNewScopeAsync(factory, new RetentionClock(Now)));
        Assert.Equal(1, concurrent.Count(item => item.Status == "completed"));
        Assert.Equal(1, concurrent.Sum(item => item.Removed));
        Assert.Equal("not_due", (await RunNewScopeAsync(factory, new RetentionClock(Now.AddHours(1)))).Status);
        Assert.Equal("completed", (await RunNewScopeAsync(factory, new RetentionClock(Now.AddHours(6)))).Status);
    }

    [PostgresFact]
    public async Task PartialDeleteFailureRollsBackBacksOffAndSuspendsUntilOperatorReset()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var user = User(Now.AddYears(-2));
        db.Users.Add(user);
        db.ExternalDeletionVerifications.Add(Token(user.Id, Now.AddDays(-1)));
        db.DataPrivacyRequests.Add(Request(user.Id, Now.AddYears(-2)));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION retention_test_failure() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'test-only rollback'; END; $$;
            CREATE TRIGGER retention_test_failure BEFORE DELETE ON data_privacy_requests
            FOR EACH ROW EXECUTE FUNCTION retention_test_failure();
            """);
        var clock = new RetentionClock(Now);
        var settings = Enabled();
        settings.MaxConsecutiveFailures = 2;
        Assert.Equal("failed", (await Processor(db, settings, clock).RunDueAsync(CancellationToken.None)).Status);
        Assert.Equal(1, await db.ExternalDeletionVerifications.CountAsync());
        Assert.Equal(1, await db.DataPrivacyRequests.CountAsync());
        Assert.Equal(0, (await db.RetentionCheckpoints.SingleAsync()).Removed);
        Assert.Equal("not_due", (await Processor(db, settings, clock).RunDueAsync(CancellationToken.None)).Status);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal("failed", (await Processor(db, settings, clock).RunDueAsync(CancellationToken.None)).Status);
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal("suspended", (await Processor(db, settings, clock).RunDueAsync(CancellationToken.None)).Status);
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER retention_test_failure ON data_privacy_requests; DROP FUNCTION retention_test_failure()");
        var checkpoint = await db.RetentionCheckpoints.SingleAsync();
        checkpoint.ConsecutiveFailures = 0;
        checkpoint.NextRunAt = null;
        await db.SaveChangesAsync();
        var recovered = await Processor(db, settings, clock).RunDueAsync(CancellationToken.None);
        Assert.Equal(2, recovered.Removed);
        Assert.Equal(0, recovered.Failed);
    }

    [PostgresFact]
    public async Task LargeBacklogIsBoundedAndHeldOldRowsCannotStarveLaterRows()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var user = User();
        var held = User();
        db.Users.AddRange(user, held);
        db.RetentionHolds.Add(new RetentionHold { Id = Guid.NewGuid(), UserId = held.Id, ReasonCode = "fraud", CreatedAt = Now });
        db.ExternalDeletionVerifications.AddRange(Enumerable.Range(0, 251).Select(index => Token(user.Id, Now.AddMinutes(-index))));
        db.ExternalDeletionVerifications.Add(Token(held.Id, Now.AddYears(-3)));
        await db.SaveChangesAsync();
        var settings = Enabled();
        settings.BatchSize = 25;
        var clock = new RetentionClock(Now);
        var total = 0;
        for (var batch = 0; batch < 11; batch++)
        {
            var result = await Processor(db, settings, clock).RunDueAsync(CancellationToken.None);
            Assert.InRange(result.Removed, 1, 25);
            Assert.Equal(1, result.Skipped);
            total += result.Removed;
            clock.Advance(TimeSpan.FromHours(6));
        }
        Assert.Equal(251, total);
        Assert.Equal(held.Id, (await db.ExternalDeletionVerifications.SingleAsync()).UserId);
        Assert.Equal(0, (await Processor(db, settings, clock).RunDueAsync(CancellationToken.None)).Removed);
        var hold = await db.RetentionHolds.SingleAsync();
        hold.ReleasedAt = clock.GetUtcNow();
        await db.SaveChangesAsync();
        clock.Advance(TimeSpan.FromHours(6));
        Assert.Equal(1, (await Processor(db, settings, clock).RunDueAsync(CancellationToken.None)).Removed);
    }

    [PostgresFact]
    public async Task HoldForeignKeyAndReasonConstraintRejectUnknownOrPersonalValues()
    {
        using var factory = CreateFactory();
        factory.InitializeDatabase();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        db.RetentionHolds.Add(new RetentionHold { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), ReasonCode = "legal", CreatedAt = Now });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.RetentionHolds.Add(new RetentionHold { Id = Guid.NewGuid(), ReasonCode = "personal@email.test", CreatedAt = Now });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static NexoraApiFactory CreateFactory() => NexoraApiFactory.CreatePostgres(Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!);
    private static RetentionOptions Enabled() => new() { Enabled = true, DryRun = false, PurgeEnabled = true };
    private static RetentionProcessor Processor(NexoraDbContext db, RetentionOptions settings, TimeProvider clock) =>
        new(db, Options.Create(settings), clock, NullLogger<RetentionProcessor>.Instance);
    private static async Task<RetentionRunResult> RunNewScopeAsync(NexoraApiFactory factory, TimeProvider clock)
    {
        using var scope = factory.Services.CreateScope();
        return await Processor(scope.ServiceProvider.GetRequiredService<NexoraDbContext>(), Enabled(), clock).RunDueAsync(CancellationToken.None);
    }
    private static ApplicationUser User(DateTimeOffset? deletedAt = null)
    {
        var id = Guid.NewGuid();
        return new ApplicationUser { Id = id, UserName = $"test-{id:N}", NormalizedUserName = $"TEST-{id:N}", CreatedAt = Now.AddYears(-3), UpdatedAt = Now, DeletedAt = deletedAt, IsActive = deletedAt is null };
    }
    private static ExternalDeletionVerification Token(Guid userId, DateTimeOffset expiry) =>
        new() { Id = Guid.NewGuid(), UserId = userId, TokenHash = Guid.NewGuid().ToString("N"), CreatedAt = expiry.AddMinutes(-30), ExpiresAt = expiry };
    private static DataPrivacyRequest Request(Guid userId, DateTimeOffset completion) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Type = "account_deletion", Status = PrivacyValues.Completed, IdempotencyKey = Guid.NewGuid().ToString("N"), RequestedAt = completion.AddDays(-1), UpdatedAt = completion, CompletedAt = completion };
    private sealed class RetentionClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }
}
