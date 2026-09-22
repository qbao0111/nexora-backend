using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Auth;
using Nexora.Business.Authorization;
using Nexora.Business.Billing;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class AdminDashboardApiTests
{
    [Fact]
    public async Task DashboardRequiresAdminAndUsesFulfilledUpdatedAtWithCurrencyIsolation()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();

        using var anonymous = await client.GetAsync("/api/v1/admin/dashboard");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        var member = await RegisterAsync(client, "dashboard-member@example.test");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", member.AccessToken);
        using var forbidden = await client.GetAsync("/api/v1/admin/dashboard");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        var adminAccount = await RegisterAsync(client, "dashboard-admin@example.test");
        var adminToken = await MakeAdminAsync(factory, adminAccount.UserId, adminAccount.Email);
        await SeedOrdersAsync(factory, member.UserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var response = await client.GetAsync("/api/v1/admin/dashboard?granularity=day&from=2026-09-02&to=2026-09-02&currency=VND");
        var responseBody = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, responseBody);
        using var document = JsonDocument.Parse(responseBody);
        var data = document.RootElement.GetProperty("data");
        Assert.Equal("Asia/Ho_Chi_Minh", data.GetProperty("range").GetProperty("timeZone").GetString());
        Assert.Equal("2026-09-02", data.GetProperty("range").GetProperty("from").GetString());

        var summary = data.GetProperty("summary");
        Assert.Equal(2, summary.GetProperty("totalUsers").GetInt32());
        Assert.Equal(1, summary.GetProperty("activeUsers").GetInt32());
        Assert.Equal(1, summary.GetProperty("inactiveUsers").GetInt32());
        Assert.Equal(1, summary.GetProperty("pendingTransactions").GetInt32());
        Assert.Equal(1, summary.GetProperty("failedTransactions").GetInt32());
        Assert.Equal(2, summary.GetProperty("fulfilledTransactionsInPeriod").GetInt32());
        var periodRevenue = summary.GetProperty("periodRevenueByCurrency").EnumerateArray().ToArray();
        Assert.Equal(2, periodRevenue.Length);
        var vndRevenue = Assert.Single(periodRevenue, item => item.GetProperty("currency").GetString() == "VND");
        Assert.Equal(200_000, vndRevenue.GetProperty("amountMinor").GetInt64());
        Assert.Equal(200_000, data.GetProperty("revenueSeries")[0].GetProperty("amountMinor").GetInt64());
        Assert.Equal(1, data.GetProperty("revenueSeries")[0].GetProperty("transactionCount").GetInt32());
        Assert.Equal("VND", data.GetProperty("revenueSeries")[0].GetProperty("currency").GetString());
        Assert.Equal("2026-09-02T00:00:00+07:00", data.GetProperty("revenueSeries")[0].GetProperty("bucketStart").GetString());
        Assert.Equal(1, data.GetProperty("userGrowthSeries")[0].GetProperty("cumulativeUsers").GetInt32());
        Assert.DoesNotContain(data.GetProperty("revenueByPlan").EnumerateArray(), item => item.GetProperty("planCode").GetString() == "failed-plan");

        using var zeroBucketResponse = await client.GetAsync("/api/v1/admin/dashboard?granularity=day&from=2026-09-02&to=2026-09-03&currency=VND");
        using var zeroBucketDocument = JsonDocument.Parse(await zeroBucketResponse.Content.ReadAsStringAsync());
        var zeroBuckets = zeroBucketDocument.RootElement.GetProperty("data").GetProperty("revenueSeries").EnumerateArray().ToArray();
        Assert.Equal(2, zeroBuckets.Length);
        Assert.Equal(0, zeroBuckets[1].GetProperty("amountMinor").GetInt64());

        using var monthResponse = await client.GetAsync("/api/v1/admin/dashboard?granularity=month&from=2026-08-01&to=2026-10-31&currency=VND");
        using var monthDocument = JsonDocument.Parse(await monthResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, monthDocument.RootElement.GetProperty("data").GetProperty("revenueSeries").GetArrayLength());

        using var yearResponse = await client.GetAsync("/api/v1/admin/dashboard?granularity=year&from=2025-01-01&to=2027-12-31&currency=VND");
        using var yearDocument = JsonDocument.Parse(await yearResponse.Content.ReadAsStringAsync());
        Assert.Equal(3, yearDocument.RootElement.GetProperty("data").GetProperty("revenueSeries").GetArrayLength());
    }

    [Fact]
    public async Task TransactionsUseStableCreatedAtAndIdCursorAndNeverExposeCheckoutSecrets()
    {
        using var factory = new NexoraApiFactory();
        factory.InitializeDatabase();
        using var client = factory.CreateHttpsClient();
        var member = await RegisterAsync(client, "cursor-member@example.test");
        var admin = await RegisterAsync(client, "cursor-admin@example.test");
        var adminToken = await MakeAdminAsync(factory, admin.UserId, admin.Email);
        await SeedTiedOrdersAsync(factory, member.UserId);
        await MarkDeletedAsync(factory, member.UserId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        using var filteredResponse = await client.GetAsync(
            "/api/v1/admin/transactions?status=fulfilled&planCode=pro&search=provider&from=2026-09-20&to=2026-09-20&pageSize=10");
        var filteredBody = await filteredResponse.Content.ReadAsStringAsync();
        Assert.True(filteredResponse.IsSuccessStatusCode, filteredBody);
        using var filteredDocument = JsonDocument.Parse(filteredBody);
        Assert.Equal(2, filteredDocument.RootElement.GetProperty("data").GetProperty("items").GetArrayLength());

        using var firstResponse = await client.GetAsync("/api/v1/admin/transactions?pageSize=1&status=fulfilled");
        var firstBody = await firstResponse.Content.ReadAsStringAsync();
        Assert.True(firstResponse.IsSuccessStatusCode, firstBody);
        Assert.DoesNotContain("checkoutUrl", firstBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("featuresSnapshot", firstBody, StringComparison.OrdinalIgnoreCase);
        using var firstDocument = JsonDocument.Parse(firstBody);
        var firstData = firstDocument.RootElement.GetProperty("data");
        var firstId = firstData.GetProperty("items")[0].GetProperty("id").GetGuid();
        var cursor = firstData.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrWhiteSpace(cursor));

        using var secondResponse = await client.GetAsync($"/api/v1/admin/transactions?pageSize=1&status=fulfilled&cursor={Uri.EscapeDataString(cursor!)}");
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using var secondDocument = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        var secondId = secondDocument.RootElement.GetProperty("data").GetProperty("items")[0].GetProperty("id").GetGuid();
        Assert.NotEqual(firstId, secondId);
        Assert.True(firstId.CompareTo(secondId) > 0);
    }

    private static async Task SeedOrdersAsync(NexoraApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var priceId = await db.PlanPrices.Select(item => item.Id).FirstAsync();
        db.Orders.AddRange(
            Order(userId, priceId, "previous-day", 100_000, "VND", BillingValues.Fulfilled,
                At("2026-09-01T10:00:00Z"), At("2026-09-01T16:59:59Z")),
            Order(userId, priceId, "included", 200_000, "VND", BillingValues.Fulfilled,
                At("2026-09-01T10:00:00Z"), At("2026-09-01T17:00:00Z")),
            Order(userId, priceId, "usd-plan", 50, "USD", BillingValues.Fulfilled,
                At("2026-09-01T10:00:00Z"), At("2026-09-01T18:00:00Z")),
            Order(userId, priceId, "failed-plan", 999_000, "VND", BillingValues.Failed,
                At("2026-09-02T01:00:00Z"), At("2026-09-02T02:00:00Z")),
            Order(userId, priceId, "pending-plan", 300_000, "VND", BillingValues.Pending,
                At("2026-09-02T01:00:00Z"), At("2026-09-02T02:00:00Z")),
            Order(userId, priceId, "processing-plan", 400_000, "VND", BillingValues.Processing,
                At("2026-09-02T01:00:00Z"), At("2026-09-02T02:00:00Z")));
        var user = await db.Users.SingleAsync(item => item.Id == userId);
        user.CreatedAt = At("2026-08-31T00:00:00Z");
        user.IsActive = false;
        await db.SaveChangesAsync();
    }

    private static async Task SeedTiedOrdersAsync(NexoraApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var priceId = await db.PlanPrices.Select(item => item.Id).FirstAsync();
        var timestamp = At("2026-09-20T03:00:00Z");
        db.Orders.AddRange(
            Order(userId, priceId, "pro", 10_000, "VND", BillingValues.Fulfilled, timestamp, timestamp,
                Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"), "provider-a"),
            Order(userId, priceId, "pro", 20_000, "VND", BillingValues.Fulfilled, timestamp, timestamp,
                Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), "provider-b"));
        await db.SaveChangesAsync();
    }

    private static async Task MarkDeletedAsync(NexoraApiFactory factory, Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var user = await db.Users.SingleAsync(item => item.Id == userId);
        user.DeletedAt = At("2026-09-21T00:00:00Z");
        await db.SaveChangesAsync();
    }

    private static Order Order(Guid userId, Guid priceId, string planCode, long amount, string currency, string status,
        DateTimeOffset createdAt, DateTimeOffset updatedAt, Guid? id = null, string? providerTransactionId = null) => new()
        {
            Id = id ?? Guid.NewGuid(),
            UserId = userId,
            PlanPriceId = priceId,
            PlanCodeSnapshot = planCode,
            AmountMinor = amount,
            Currency = currency,
            Status = status,
            PaymentProvider = "test",
            ProviderTransactionId = providerTransactionId ?? $"txn-{Guid.NewGuid():N}",
            CheckoutUrl = "https://example.test/private-checkout",
            FeaturesSnapshot = "[]",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt
        };

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private static async Task<Account> RegisterAsync(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123", displayName = "Admin test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), email, data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<string> MakeAdminAsync(NexoraApiFactory factory, Guid userId, string email)
    {
        using (var scope = factory.Services.CreateScope())
        {
            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (!await roleManager.RoleExistsAsync(RoleNames.Admin)) await roleManager.CreateAsync(new IdentityRole<Guid>(RoleNames.Admin));
            var user = await userManager.FindByIdAsync(userId.ToString());
            await userManager.AddToRoleAsync(user!, RoleNames.Admin);
        }
        using var client = factory.CreateHttpsClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private sealed record Account(Guid UserId, string Email, string AccessToken);
}
