using Microsoft.EntityFrameworkCore;
using Nexora.Business.Admin;
using Nexora.Business.Billing;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

[Collection("PostgreSQL primary resume")]
public sealed class AdminDashboardPostgresTests
{
    [PostgresFact]
    public async Task DashboardAggregationAndTransactionPagingRunOnPostgres()
    {
        var connectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var options = new DbContextOptionsBuilder<NexoraDbContext>().UseNpgsql(connectionString).Options;
        await using var db = new NexoraDbContext(options);
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();

        var userId = Guid.NewGuid();
        var priceId = Guid.Parse("10000000-0000-0000-0000-000000000004");
        var fulfilledAt = new DateTimeOffset(2026, 9, 2, 1, 0, 0, TimeSpan.Zero);
        db.Users.Add(new ApplicationUser
        {
            Id = userId,
            UserName = "admin-report@example.test",
            NormalizedUserName = "ADMIN-REPORT@EXAMPLE.TEST",
            Email = "admin-report@example.test",
            NormalizedEmail = "ADMIN-REPORT@EXAMPLE.TEST",
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = fulfilledAt.AddDays(-10),
            UpdatedAt = fulfilledAt,
            SecurityStamp = Guid.NewGuid().ToString("N")
        });
        db.Orders.Add(new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PlanPriceId = priceId,
            PlanCodeSnapshot = "pro",
            AmountMinor = 490_000,
            Currency = "VND",
            Status = BillingValues.Fulfilled,
            PaymentProvider = "test",
            ProviderTransactionId = "postgres-report-reference",
            CheckoutUrl = "https://example.test/private",
            FeaturesSnapshot = "[]",
            CreatedAt = fulfilledAt.AddMinutes(-5),
            UpdatedAt = fulfilledAt
        });
        await db.SaveChangesAsync();

        var service = new AdminDashboardService(db, new FixedTimeProvider(fulfilledAt));
        var dashboard = await service.GetDashboardAsync(
            new AdminDashboardQuery("day", new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 2), "VND"),
            CancellationToken.None);
        var revenue = Assert.Single(dashboard.RevenueSeries);
        Assert.Equal(490_000, revenue.AmountMinor);
        Assert.Equal(1, revenue.TransactionCount);

        var page = await service.GetTransactionsAsync(
            new AdminTransactionQuery("postgres-report", "fulfilled", "pro", "VND",
                new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 2), null, 1),
            CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Null(page.NextCursor);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
