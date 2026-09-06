using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;

namespace Nexora.IntegrationTests;

public sealed class BillingApiTests : IClassFixture<NexoraApiFactory>
{
    private const string TestWebhookKey = "phase2-test-webhook-key-material";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly NexoraApiFactory _factory;

    public BillingApiTests(NexoraApiFactory factory)
    {
        _factory = factory;
        _factory.InitializeDatabase();
    }

    [Fact]
    public async Task DuplicateFakeWebhookCreatesOnePaymentEventAndEntitlementT04()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        using (var plans = await client.GetAsync("/api/v1/plans"))
        {
            Assert.Equal(HttpStatusCode.OK, plans.StatusCode);
            using var plansBody = JsonDocument.Parse(await plans.Content.ReadAsStringAsync());
            var catalogue = plansBody.RootElement.GetProperty("data").EnumerateArray().ToArray();
            var free = Assert.Single(catalogue, item => item.GetProperty("code").GetString() == "free");
            Assert.Equal(0, free.GetProperty("prices")[0].GetProperty("amountMinor").GetInt64());
            Assert.Equal(JsonValueKind.Null, free.GetProperty("prices")[0].GetProperty("durationDays").ValueKind);
            Assert.Equal(1, free.GetProperty("prices")[0].GetProperty("interviewQuota").GetInt32());
            var basic = Assert.Single(catalogue, item => item.GetProperty("code").GetString() == "basic");
            Assert.Equal(49_000, basic.GetProperty("prices")[0].GetProperty("amountMinor").GetInt64());
            Assert.Equal(3, basic.GetProperty("prices")[0].GetProperty("durationDays").GetInt32());
            Assert.Equal(3, basic.GetProperty("prices")[0].GetProperty("interviewQuota").GetInt32());
            var weekly = Assert.Single(catalogue, item => item.GetProperty("code").GetString() == "weekly");
            Assert.Equal(189_000, weekly.GetProperty("prices")[0].GetProperty("amountMinor").GetInt64());
            Assert.Equal(14, weekly.GetProperty("prices")[0].GetProperty("durationDays").GetInt32());
            Assert.Equal(20, weekly.GetProperty("prices")[0].GetProperty("interviewQuota").GetInt32());
            var pro = Assert.Single(catalogue, item => item.GetProperty("code").GetString() == "pro");
            Assert.Equal(599_000, pro.GetProperty("prices")[0].GetProperty("amountMinor").GetInt64());
            Assert.Equal(90, pro.GetProperty("prices")[0].GetProperty("durationDays").GetInt32());
            Assert.Equal(JsonValueKind.Null, pro.GetProperty("prices")[0].GetProperty("interviewQuota").ValueKind);
        }
        var price = await SeedPlanPriceAsync(interviewQuota: 3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions")
        {
            Content = JsonContent.Create(new { planPriceId = price.Id, amountMinor = 1 })
        };
        forged.Headers.Add("Idempotency-Key", "forged-price");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(forged)).StatusCode);

        var checkout = await CreateCheckoutAsync(client, price.Id, "checkout-t04");
        var duplicateCheckout = await CreateCheckoutAsync(client, price.Id, "checkout-t04");
        Assert.Equal(checkout.OrderId, duplicateCheckout.OrderId);
        Assert.Equal(price.AmountMinor, checkout.AmountMinor);
        var otherPrice = await SeedPlanPriceAsync(interviewQuota: 2);
        using (var conflict = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions") { Content = JsonContent.Create(new { planPriceId = otherPrice.Id }) })
        {
            conflict.Headers.Add("Idempotency-Key", "checkout-t04");
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(conflict)).StatusCode);
        }

        var webhook = await CreateWebhookAsync(checkout.OrderId, "evt-t04");
        var deliveries = await Task.WhenAll(SendWebhookAsync(client, webhook), SendWebhookAsync(client, webhook));
        Assert.All(deliveries, delivery => Assert.Equal(HttpStatusCode.NoContent, delivery.StatusCode));
        foreach (var delivery in deliveries) delivery.Dispose();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Subscriptions.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Entitlements.CountAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free"));
        Assert.Equal(BillingValues.Fulfilled, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);

        using var me = await client.GetAsync("/api/v1/me");
        using var body = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("data").GetProperty("billing")
            .GetProperty("entitlement").GetProperty("available").GetInt32());
    }

    [Fact]
    public async Task InvalidFakeWebhookSignatureDoesNotChangeOrderT05()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "checkout-t05");
        var webhook = await CreateWebhookAsync(checkout.OrderId, "evt-t05");

        using var request = WebhookRequest(webhook.Body, webhook.Timestamp, "00");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.PaymentEvents.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free").ToListAsync());
        Assert.Equal(BillingValues.Pending, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
    }

    [Fact]
    public async Task PaymentReferenceMismatchDoesNotGrantEntitlement()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "checkout-mismatch");
        var webhook = await CreateWebhookAsync(checkout.OrderId, "evt-mismatch", amountDelta: 1);

        using var response = await SendWebhookAsync(client, webhook);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.PaymentEvents.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free").ToListAsync());
    }

    [Fact]
    public async Task CheckoutStatusIsOwnerScoped()
    {
        using var client = _factory.CreateHttpsClient();
        var owner = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", owner.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "checkout-owner");

        using (var status = await client.GetAsync($"/api/v1/checkout-sessions/{checkout.OrderId}"))
        {
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        }

        var other = await RegisterAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", other.AccessToken);
        using var forbidden = await client.GetAsync($"/api/v1/checkout-sessions/{checkout.OrderId}");
        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
    }

    [Fact]
    public async Task ConcurrentQuotaReserveAllowsOneAndLedgerTransitionsRemainImmutableT03()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "checkout-t03");
        Assert.Equal(HttpStatusCode.NoContent, (await SendWebhookAsync(client, await CreateWebhookAsync(checkout.OrderId, "evt-t03"))).StatusCode);

        var reservations = await Task.WhenAll(
            TryReserveAsync(account.UserId, "session-a", "reserve-a"),
            TryReserveAsync(account.UserId, "session-b", "reserve-b"));
        var reservation = Assert.Single(reservations, item => item is not null)!;

        await WithBillingServiceAsync(service => service.ConsumeReservationAsync(account.UserId, reservation.EventId, CancellationToken.None));
        await WithBillingServiceAsync(service => service.ConsumeReservationAsync(account.UserId, reservation.EventId, CancellationToken.None));
        await WithBillingServiceAsync(service => service.AdjustInterviewQuotaAsync(account.UserId, 1, "T-03 test credit", "adjust-t03", CancellationToken.None));
        var second = await WithBillingServiceAsync(service => service.ReserveInterviewAsync(account.UserId, "session-c", "reserve-c", CancellationToken.None));
        await WithBillingServiceAsync(service => service.VoidReservationAsync(account.UserId, second.EventId, CancellationToken.None));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var entitlement = await db.Entitlements.SingleAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free");
        Assert.Equal(0, entitlement.Reserved);
        Assert.Equal(1, entitlement.Consumed);
        Assert.Equal(1, entitlement.Adjustment);
        Assert.Equal(5, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Reserve &&
            (item.SourceId == "session-a" || item.SourceId == "session-b")));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Consume));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Void));
        Assert.Equal(1, await db.UsageEvents.CountAsync(item => item.UserId == account.UserId && item.Action == BillingValues.Adjustment));
    }

    private async Task<UsageReservation?> TryReserveAsync(Guid userId, string sourceId, string key)
    {
        try { return await WithBillingServiceAsync(service => service.ReserveInterviewAsync(userId, sourceId, key, CancellationToken.None)); }
        catch (BusinessException exception) when (exception.Code == "QUOTA_EXCEEDED") { return null; }
    }

    private async Task<PlanPrice> SeedPlanPriceAsync(int interviewQuota)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"test-{Guid.NewGuid():N}", Name = "Synthetic test plan", IsActive = true, CreatedAt = now };
        var price = new PlanPrice
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            AmountMinor = 123_000,
            Currency = "VND",
            DurationDays = 14,
            InterviewQuota = interviewQuota,
            IsActive = true,
            CreatedAt = now
        };
        db.AddRange(plan, price);
        await db.SaveChangesAsync();
        return price;
    }

    private static async Task<CheckoutTestResponse> CreateCheckoutAsync(HttpClient client, Guid planPriceId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions") { Content = JsonContent.Create(new { planPriceId }) };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return new CheckoutTestResponse(data.GetProperty("orderId").GetGuid(), data.GetProperty("amountMinor").GetInt64());
    }

    private async Task<Webhook> CreateWebhookAsync(Guid orderId, string eventId, long amountDelta = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            eventId,
            orderId,
            transactionId = order.ProviderTransactionId,
            amountMinor = order.AmountMinor + amountDelta,
            currency = order.Currency,
            status = "paid",
            occurredAt = DateTimeOffset.UtcNow
        }, JsonOptions);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TestWebhookKey));
        var signature = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(body)}"))).ToLowerInvariant();
        return new Webhook(body, timestamp, signature);
    }

    private static async Task<HttpResponseMessage> SendWebhookAsync(HttpClient client, Webhook webhook)
    {
        using var request = WebhookRequest(webhook.Body, webhook.Timestamp, webhook.Signature);
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage WebhookRequest(byte[] body, string timestamp, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/fake") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Payment-Timestamp", timestamp);
        request.Headers.Add("X-Payment-Signature", signature);
        return request;
    }

    private async Task<T> WithBillingServiceAsync<T>(Func<IBillingService, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<IBillingService>());
    }

    private async Task WithBillingServiceAsync(Func<IBillingService, Task> action)
    {
        using var scope = _factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<IBillingService>());
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"billing-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "Billing candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private sealed record Account(Guid UserId, string AccessToken);
    private sealed record CheckoutTestResponse(Guid OrderId, long AmountMinor);
    private sealed record Webhook(byte[] Body, string Timestamp, string Signature);
}
