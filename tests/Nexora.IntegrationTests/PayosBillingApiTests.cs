using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Authorization;
using Nexora.Business.Billing;
using Nexora.Data.Billing;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;
using Nexora.Integrations.Payments;
using PayOS.Crypto;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;

namespace Nexora.IntegrationTests;

public sealed class PayosBillingApiTests : IDisposable
{
    private const string ClientId = "payos-integration-test-client";
    private const string ApiKey = "payos-integration-test-api-key";
    private const string ChecksumKey = "payos-integration-test-checksum-key";
    private readonly PayosHttpHandler _handler = new();
    private readonly NexoraApiFactory _factory;

    public PayosBillingApiTests()
    {
        _factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["Billing:Payment:Provider"] = "payos",
            ["Billing:Payos:ClientId"] = ClientId,
            ["Billing:Payos:ApiKey"] = ApiKey,
            ["Billing:Payos:ChecksumKey"] = ChecksumKey,
            ["Billing:Payos:ReturnUrl"] = "https://frontend.example.test/payment/success",
            ["Billing:Payos:CancelUrl"] = "https://frontend.example.test/payment/cancel",
            ["Billing:Payos:TimeoutSeconds"] = "15"
        }, services =>
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(provider => provider.GetRequiredService<PayosPaymentProvider>());
            services.AddHttpClient<PayosPaymentProvider>().ConfigurePrimaryHttpMessageHandler(_ => _handler);
        });
        _factory.InitializeDatabase();
    }

    [Fact]
    public void ValidPayosConfigurationStartsWithPayosProvider()
    {
        Assert.IsType<PayosPaymentProvider>(_factory.Services.GetRequiredService<IPaymentProvider>());
    }

    [Fact]
    public async Task CheckoutCreatesPayosPaymentLinkOnceAndReturnsPersistedActionForRetryAndRead()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var first = await CreateCheckoutAsync(client, price.Id, "payos-checkout");
        Assert.Single(_handler.CreatedOrderCodes);
        Assert.Equal("Basic", _handler.CreatedDescriptions[0]);
        var second = await CreateCheckoutAsync(client, price.Id, "payos-checkout");

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(first.CheckoutUrl, second.CheckoutUrl);
        Assert.Single(_handler.CreatedOrderCodes);

        using (var existing = await client.GetAsync($"/api/v1/checkout-sessions/{first.OrderId}"))
        {
            Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
            using var json = JsonDocument.Parse(await existing.Content.ReadAsStringAsync());
            var action = json.RootElement.GetProperty("data").GetProperty("checkout");
            Assert.Equal("GET", action.GetProperty("method").GetString());
            Assert.Equal(first.CheckoutUrl, action.GetProperty("url").GetString());
            Assert.Empty(action.GetProperty("fields").EnumerateArray());
        }
        Assert.Single(_handler.CreatedOrderCodes);

        using var scope = _factory.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Orders.SingleAsync(item => item.Id == first.OrderId);
        Assert.Equal("payos", order.PaymentProvider);
        Assert.True(long.TryParse(order.ProviderTransactionId, NumberStyles.None, CultureInfo.InvariantCulture, out var orderCode));
        Assert.InRange(orderCode, 1L, 9_007_199_254_740_991L);
        Assert.Equal(order.ProviderTransactionId, _handler.CreatedOrderCodes[0]);
        Assert.NotNull(order.ExpiresAt);
        Assert.Contains(order.ExpiresAt.Value.ToUnixTimeSeconds(), _handler.CreatedExpirations);
    }

    [Fact]
    public async Task ExpiredPayosOrderIsCancelledBestEffortAfterInternalTransition()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "payos-expiration");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            var order = await db.Orders.SingleAsync(item => item.Id == checkout.OrderId);
            order.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var billing = scope.ServiceProvider.GetRequiredService<IBillingService>();
            Assert.Equal(1, await billing.ExpirePendingPaymentsAsync(CancellationToken.None));
        }

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var expired = await verifyDb.Orders.SingleAsync(item => item.Id == checkout.OrderId);
        Assert.Equal(BillingValues.Expired, expired.Status);
        Assert.Contains(expired.ProviderTransactionId, _handler.CancelledOrderCodes);
    }

    [Fact]
    public async Task AdminCanEditBasicPriceAndReadUpdatedValueAfterReload()
    {
        using var registrationClient = _factory.CreateHttpsClient();
        var account = await RegisterAsync(registrationClient);
        var adminToken = await MakeAdminAsync(account);
        var price = await FindPlanPriceAsync("basic");

        using var adminClient = _factory.CreateHttpsClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var update = await adminClient.PatchAsJsonAsync($"/api/v1/admin/plan-prices/{price.Id}", new
        {
            amountMinor = 177_000,
            currency = "VND",
            durationDays = 14,
            interviewQuota = 3,
            isActive = true
        });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        using var reload = await adminClient.GetAsync("/api/v1/admin/plans");
        Assert.Equal(HttpStatusCode.OK, reload.StatusCode);
        using var json = JsonDocument.Parse(await reload.Content.ReadAsStringAsync());
        var updatedView = json.RootElement.GetProperty("data").EnumerateArray()
            .SelectMany(plan => plan.GetProperty("prices").EnumerateArray())
            .Single(priceView => priceView.GetProperty("id").GetGuid() == price.Id);
        Assert.Equal(177_000, updatedView.GetProperty("amountMinor").GetInt64());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(177_000, (await db.PlanPrices.SingleAsync(item => item.Id == price.Id)).AmountMinor);
    }

    [Fact]
    public async Task AdminCreatedPlanWithNewCodeCanCheckoutAndFulfillMatchingSubscription()
    {
        using var registrationClient = _factory.CreateHttpsClient();
        var adminAccount = await RegisterAsync(registrationClient);
        var adminToken = await MakeAdminAsync(adminAccount);
        var planCode = $"career-{Guid.NewGuid():N}";
        var planName = "Career Starter";

        using var adminClient = _factory.CreateHttpsClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        using var createPlan = await adminClient.PostAsJsonAsync("/api/v1/admin/plans", new
        {
            code = planCode,
            name = planName,
            description = "A package created during the payment flow test",
            isHighlighted = false
        });
        Assert.Equal(HttpStatusCode.Created, createPlan.StatusCode);
        using var createdPlanJson = JsonDocument.Parse(await createPlan.Content.ReadAsStringAsync());
        var createdPlan = createdPlanJson.RootElement.GetProperty("data");
        var planId = createdPlan.GetProperty("id").GetGuid();
        Assert.Equal(planName, createdPlan.GetProperty("name").GetString());

        using var createPrice = await adminClient.PostAsJsonAsync($"/api/v1/admin/plans/{planId}/prices", new
        {
            amountMinor = 123_000,
            currency = "VND",
            durationDays = 14,
            interviewQuota = 7
        });
        Assert.Equal(HttpStatusCode.Created, createPrice.StatusCode);
        using var createdPriceJson = JsonDocument.Parse(await createPrice.Content.ReadAsStringAsync());
        var createdPrice = createdPriceJson.RootElement.GetProperty("data").GetProperty("prices").EnumerateArray()
            .First(item => item.GetProperty("amountMinor").GetInt64() == 123_000);
        var priceId = createdPrice.GetProperty("id").GetGuid();

        using var catalogue = await adminClient.GetAsync("/api/v1/plans");
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
        using var catalogueJson = JsonDocument.Parse(await catalogue.Content.ReadAsStringAsync());
        var publicPlan = catalogueJson.RootElement.GetProperty("data").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == planId);
        Assert.Equal(planName, publicPlan.GetProperty("name").GetString());
        Assert.Contains(publicPlan.GetProperty("prices").EnumerateArray(), item => item.GetProperty("id").GetGuid() == priceId);

        using var candidateClient = _factory.CreateHttpsClient();
        var candidate = await RegisterAsync(candidateClient);
        candidateClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", candidate.AccessToken);
        var checkout = await CreateCheckoutAsync(candidateClient, priceId, "payos-admin-created-plan");
        Assert.NotEmpty(_handler.CreatedDescriptions);
        Assert.Equal(planName, _handler.CreatedDescriptions[^1]);

        using var paid = await SendWebhookAsync(candidateClient, checkout.OrderId, reference: "PAYOS-NEW-PLAN-REFERENCE");
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var order = await db.Orders.SingleAsync(item => item.Id == checkout.OrderId);
        var subscription = await db.Subscriptions.SingleAsync(item => item.OrderId == checkout.OrderId);
        var entitlement = await db.Entitlements.SingleAsync(item => item.SubscriptionId == subscription.Id);
        Assert.Equal(priceId, order.PlanPriceId);
        Assert.Equal(planCode, order.PlanCodeSnapshot);
        Assert.Equal(candidate.UserId, order.UserId);
        Assert.Equal(order.Id, subscription.OrderId);
        Assert.Equal(planCode, entitlement.PlanCodeSnapshot);
    }

    [Fact]
    public async Task ValidWebhookFulfillsOnceAndDuplicateIsIdempotent()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "payos-webhook");

        using var paid = await SendWebhookAsync(client, checkout.OrderId);
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        using var duplicate = await SendWebhookAsync(client, checkout.OrderId);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Subscriptions.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Entitlements.CountAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free"));
        Assert.Equal(BillingValues.Fulfilled, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
    }

    [Theory]
    [InlineData(PaymentLinkStatus.Pending, 0L)]
    [InlineData(PaymentLinkStatus.Processing, 0L)]
    [InlineData(PaymentLinkStatus.Underpaid, 50_000L)]
    public async Task ValidNonFinalWebhookIsAcknowledgedAndKeepsOrderPendingWithoutEntitlement(PaymentLinkStatus providerStatus, long amountPaid)
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, $"payos-non-final-{providerStatus}");
        _handler.QueryStatus = providerStatus;
        _handler.QueryAmountPaid = amountPaid;

        using var response = await SendWebhookAsync(client, checkout.OrderId, amount: amountPaid == 0 ? 123_000 : amountPaid);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(BillingValues.Pending, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
        Assert.Empty(await db.PaymentEvents.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
        Assert.Empty(await db.Subscriptions.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free").ToListAsync());
    }

    [Fact]
    public async Task SameWebhookCanBeReconciledAfterTransientProcessingStatus()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "payos-eventual-consistency");
        var payload = await BuildWebhookForOrderAsync(checkout.OrderId, reference: "TX-1");

        _handler.QueryStatus = PaymentLinkStatus.Processing;
        _handler.QueryAmountPaid = 0;
        using (var processing = await SendWebhookPayloadAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, processing.StatusCode);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(BillingValues.Pending, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
            Assert.Empty(await db.PaymentEvents.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
            Assert.Empty(await db.Subscriptions.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
            Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free").ToListAsync());
        }

        _handler.QueryStatus = PaymentLinkStatus.Paid;
        _handler.QueryAmountPaid = 123_000;
        using (var paid = await SendWebhookPayloadAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        }

        using (var replay = await SendWebhookPayloadAsync(client, payload))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        }

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(BillingValues.Fulfilled, (await finalDb.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
        Assert.Equal(1, await finalDb.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await finalDb.Subscriptions.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await finalDb.Entitlements.CountAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free"));
    }

    [Fact]
    public async Task SplitPaymentWebhooksFulfillOnlyAfterAggregateAmountIsPaidAndRemainIdempotent()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "payos-split-payment");

        _handler.QueryStatus = PaymentLinkStatus.Underpaid;
        _handler.QueryAmountPaid = 50_000;
        using (var partial = await SendWebhookAsync(client, checkout.OrderId, amount: 50_000, reference: "PART-1"))
        {
            Assert.Equal(HttpStatusCode.OK, partial.StatusCode);
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
            Assert.Equal(BillingValues.Pending, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
            Assert.Empty(await db.PaymentEvents.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
            Assert.Empty(await db.Subscriptions.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
            Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free").ToListAsync());
        }

        using (var replayPartial = await SendWebhookAsync(client, checkout.OrderId, amount: 50_000, reference: "PART-1"))
        {
            Assert.Equal(HttpStatusCode.OK, replayPartial.StatusCode);
        }

        _handler.QueryStatus = PaymentLinkStatus.Paid;
        _handler.QueryAmountPaid = 123_000;
        using (var paid = await SendWebhookAsync(client, checkout.OrderId, amount: 73_000, reference: "PART-2"))
        {
            Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        }

        using (var replayPaid = await SendWebhookAsync(client, checkout.OrderId, amount: 73_000, reference: "PART-2"))
        {
            Assert.Equal(HttpStatusCode.OK, replayPaid.StatusCode);
        }

        using var finalScope = _factory.Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(BillingValues.Fulfilled, (await finalDb.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
        Assert.Equal(1, await finalDb.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await finalDb.Subscriptions.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await finalDb.Entitlements.CountAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free"));
    }

    [Fact]
    public async Task InvalidWebhookSignatureAndMismatchedPaymentLinkAmountDoNotFulfill()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "payos-invalid-webhook");

        using var invalidSignature = await SendWebhookAsync(client, checkout.OrderId, signature: "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidSignature.StatusCode);
        _handler.QueryAmount = 1;
        using var wrongAmount = await SendWebhookAsync(client, checkout.OrderId);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAmount.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.PaymentEvents.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
        Assert.Equal(BillingValues.Pending, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
    }

    [Theory]
    [InlineData(PaymentLinkStatus.Pending, "pending")]
    [InlineData(PaymentLinkStatus.Paid, "fulfilled")]
    [InlineData(PaymentLinkStatus.Cancelled, "failed")]
    public async Task RefreshUsesPayosQueryStatus(PaymentLinkStatus providerStatus, string expectedStatus)
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, $"payos-refresh-{providerStatus}");
        _handler.QueryStatus = providerStatus;

        using var response = await client.PostAsync($"/api/v1/checkout-sessions/{checkout.OrderId}/refresh", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task VerifiedPayosDashboardProbeReturnsSuccessWithoutBillingMutation()
    {
        using var client = _factory.CreateHttpsClient();
        using var response = await SendWebhookPayloadAsync(client, BuildWebhook(orderCode: 123, amount: 3_000));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.PaymentEvents.ToListAsync());
    }

    private async Task<PlanPrice> SeedPlanPriceAsync(int quota, string planCode = "basic")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = await db.Plans.SingleAsync(item => item.Code == planCode);
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 123_000, Currency = "VND", DurationDays = 14, InterviewQuota = quota, IsActive = true, CreatedAt = now };
        db.PlanPrices.Add(price);
        await db.SaveChangesAsync();
        return price;
    }

    private async Task<PlanPrice> FindPlanPriceAsync(string planCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        return await db.PlanPrices.SingleAsync(item => item.Plan.Code == planCode);
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"payos-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123", displayName = "payOS candidate" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await TestEmailInbox.VerifyAsync(client, email);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), email, data.GetProperty("accessToken").GetString()!);
    }

    private async Task<string> MakeAdminAsync(Account account)
    {
        using var scope = _factory.Services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await roleManager.RoleExistsAsync(RoleNames.Admin) is false)
            await roleManager.CreateAsync(new IdentityRole<Guid>(RoleNames.Admin));
        var user = await userManager.FindByIdAsync(account.UserId.ToString());
        Assert.NotNull(user);
        if (await userManager.IsInRoleAsync(user!, RoleNames.Admin) is false)
            await userManager.AddToRoleAsync(user!, RoleNames.Admin);

        using var client = _factory.CreateHttpsClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = account.Email, password = "Strong!Pass123" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        using var json = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static async Task<CheckoutResponse> CreateCheckoutAsync(HttpClient client, Guid priceId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions") { Content = JsonContent.Create(new { planPriceId = priceId }) };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("payos", data.GetProperty("provider").GetString());
        var action = data.GetProperty("checkout");
        Assert.Equal("GET", action.GetProperty("method").GetString());
        Assert.StartsWith("https://pay.payos.vn/web/", action.GetProperty("url").GetString()!);
        Assert.Empty(action.GetProperty("fields").EnumerateArray());
        return new CheckoutResponse(
            data.GetProperty("orderId").GetGuid(),
            data.GetProperty("checkout").GetProperty("url").GetString()!,
            data.GetProperty("expiresAt").GetDateTimeOffset());
    }

    private async Task<HttpResponseMessage> SendWebhookAsync(
        HttpClient client,
        Guid orderId,
        string? signature = null,
        long? amount = null,
        string reference = "PAYOS-INTEGRATION-REFERENCE")
    {
        using var scope = _factory.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        return await SendWebhookPayloadAsync(client, BuildWebhook(
            long.Parse(order.ProviderTransactionId, CultureInfo.InvariantCulture),
            amount ?? order.AmountMinor,
            signature,
            reference));
    }

    private async Task<byte[]> BuildWebhookForOrderAsync(Guid orderId, long? amount = null, string reference = "PAYOS-INTEGRATION-REFERENCE")
    {
        using var scope = _factory.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        return BuildWebhook(
            long.Parse(order.ProviderTransactionId, CultureInfo.InvariantCulture),
            amount ?? order.AmountMinor,
            reference: reference);
    }

    private static async Task<HttpResponseMessage> SendWebhookPayloadAsync(HttpClient client, byte[] payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/payos") { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.SendAsync(request);
    }

    private static byte[] BuildWebhook(
        long orderCode,
        long amount,
        string? signature = null,
        string reference = "PAYOS-INTEGRATION-REFERENCE")
    {
        var data = new WebhookData
        {
            OrderCode = orderCode,
            Amount = amount,
            Description = "Nexora",
            AccountNumber = string.Empty,
            Reference = reference,
            TransactionDateTime = "2026-09-18 10:30:00",
            Currency = "VND",
            PaymentLinkId = $"link-{orderCode}",
            Code = "00",
            Description2 = "success",
            CounterAccountBankId = string.Empty,
            CounterAccountBankName = string.Empty,
            CounterAccountName = string.Empty,
            CounterAccountNumber = string.Empty,
            VirtualAccountName = string.Empty,
            VirtualAccountNumber = string.Empty
        };
        var webhook = new Webhook
        {
            Code = "00",
            Description = "success",
            Success = true,
            Data = data,
            Signature = signature ?? new CryptoProvider().CreateSignatureFromObject(data, ChecksumKey)!
        };
        return JsonSerializer.SerializeToUtf8Bytes(webhook);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record Account(Guid UserId, string Email, string AccessToken);
    private sealed record CheckoutResponse(Guid OrderId, string CheckoutUrl, DateTimeOffset ExpiresAt);

    private sealed class PayosHttpHandler : HttpMessageHandler
    {
        public List<string> CreatedOrderCodes { get; } = [];
        public List<string> CreatedDescriptions { get; } = [];
        public List<long> CreatedExpirations { get; } = [];
        public List<string> CancelledOrderCodes { get; } = [];
        public PaymentLinkStatus QueryStatus { get; set; } = PaymentLinkStatus.Paid;
        public long? QueryAmount { get; set; }
        public long? QueryAmountPaid { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post && string.Equals(request.RequestUri?.AbsolutePath, "/v2/payment-requests", StringComparison.Ordinal))
            {
                var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                var orderCode = root.GetProperty("orderCode").GetInt64();
                var amount = root.GetProperty("amount").GetInt64();
                CreatedOrderCodes.Add(orderCode.ToString(CultureInfo.InvariantCulture));
                CreatedDescriptions.Add(root.GetProperty("description").GetString() ?? string.Empty);
                CreatedExpirations.Add(root.GetProperty("expiredAt").GetInt64());
                return SignedResponse(new CreatePaymentLinkResponse
                {
                    OrderCode = orderCode,
                    Amount = amount,
                    Currency = "VND",
                    PaymentLinkId = $"link-{orderCode}",
                    Status = PaymentLinkStatus.Pending,
                    CheckoutUrl = $"https://pay.payos.vn/web/{orderCode}"
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath.EndsWith("/cancel", StringComparison.Ordinal) == true)
            {
                var orderCodeText = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)[^2];
                CancelledOrderCodes.Add(orderCodeText);
                return SignedResponse(new PaymentLink
                {
                    Id = $"link-{orderCodeText}",
                    OrderCode = long.Parse(orderCodeText, CultureInfo.InvariantCulture),
                    Amount = 123_000,
                    AmountPaid = 0,
                    AmountRemaining = 123_000,
                    Status = PaymentLinkStatus.Cancelled,
                    CreatedAt = "2026-09-18T03:30:00.000Z",
                    Transactions = []
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.StartsWith("/v2/payment-requests/", StringComparison.Ordinal) == true)
            {
                var orderCodeText = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
                var orderCode = long.Parse(orderCodeText, CultureInfo.InvariantCulture);
                var amount = QueryAmount ?? 123_000;
                var amountPaid = QueryAmountPaid ?? (QueryStatus == PaymentLinkStatus.Paid ? amount : 0);
                return SignedResponse(new PaymentLink
                {
                    Id = $"link-{orderCode}",
                    OrderCode = orderCode,
                    Amount = amount,
                    AmountPaid = amountPaid,
                    AmountRemaining = Math.Max(0L, amount - amountPaid),
                    Status = QueryStatus,
                    CreatedAt = "2026-09-18T03:30:00.000Z",
                    Transactions = []
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage SignedResponse<T>(T data)
        {
            var signature = new CryptoProvider().CreateSignatureFromObject(data!, ChecksumKey);
            var body = JsonSerializer.Serialize(new { code = "00", desc = "success", data, signature });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
