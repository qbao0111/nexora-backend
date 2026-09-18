using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Billing;
using Nexora.Data.Billing;
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
    public async Task CheckoutPersistsNumericPayosOrderCodeAndReusesItForIdempotentRetry()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var first = await CreateCheckoutAsync(client, price.Id, "payos-checkout");
        var second = await CreateCheckoutAsync(client, price.Id, "payos-checkout");

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(first.CheckoutUrl, second.CheckoutUrl);
        Assert.Equal(2, _handler.CreatedOrderCodes.Count);
        Assert.Single(_handler.CreatedOrderCodes.Distinct());

        using var scope = _factory.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Orders.SingleAsync(item => item.Id == first.OrderId);
        Assert.Equal("payos", order.PaymentProvider);
        Assert.True(long.TryParse(order.ProviderTransactionId, NumberStyles.None, CultureInfo.InvariantCulture, out var orderCode));
        Assert.InRange(orderCode, 1L, 9_007_199_254_740_991L);
        Assert.Equal(order.ProviderTransactionId, _handler.CreatedOrderCodes[0]);
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

    [Fact]
    public async Task InvalidWebhookSignatureAndWrongAmountDoNotFulfill()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "payos-invalid-webhook");

        using var invalidSignature = await SendWebhookAsync(client, checkout.OrderId, signature: "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, invalidSignature.StatusCode);
        using var wrongAmount = await SendWebhookAsync(client, checkout.OrderId, amount: 1);
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

    private async Task<PlanPrice> SeedPlanPriceAsync(int quota)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"payos-{Guid.NewGuid():N}", Name = "payOS test plan", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 123_000, Currency = "VND", DurationDays = 14, InterviewQuota = quota, IsActive = true, CreatedAt = now };
        db.AddRange(plan, price);
        await db.SaveChangesAsync();
        return price;
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
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
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
        return new CheckoutResponse(data.GetProperty("orderId").GetGuid(), data.GetProperty("checkout").GetProperty("url").GetString()!);
    }

    private async Task<HttpResponseMessage> SendWebhookAsync(HttpClient client, Guid orderId, string? signature = null, long? amount = null)
    {
        using var scope = _factory.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        return await SendWebhookPayloadAsync(client, BuildWebhook(
            long.Parse(order.ProviderTransactionId, CultureInfo.InvariantCulture),
            amount ?? order.AmountMinor,
            signature));
    }

    private static async Task<HttpResponseMessage> SendWebhookPayloadAsync(HttpClient client, byte[] payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/payos") { Content = new ByteArrayContent(payload) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await client.SendAsync(request);
    }

    private static byte[] BuildWebhook(long orderCode, long amount, string? signature = null)
    {
        var data = new WebhookData
        {
            OrderCode = orderCode,
            Amount = amount,
            Description = "Nexora",
            AccountNumber = string.Empty,
            Reference = "PAYOS-INTEGRATION-REFERENCE",
            TransactionDateTime = "2026-09-18 10:30:00",
            Currency = "VND",
            PaymentLinkId = "integration-payment-link",
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

    private sealed record Account(Guid UserId, string AccessToken);
    private sealed record CheckoutResponse(Guid OrderId, string CheckoutUrl);

    private sealed class PayosHttpHandler : HttpMessageHandler
    {
        public List<string> CreatedOrderCodes { get; } = [];
        public PaymentLinkStatus QueryStatus { get; set; } = PaymentLinkStatus.Pending;

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

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath.StartsWith("/v2/payment-requests/", StringComparison.Ordinal) == true)
            {
                var orderCodeText = request.RequestUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
                var orderCode = long.Parse(orderCodeText, CultureInfo.InvariantCulture);
                var amountPaid = QueryStatus == PaymentLinkStatus.Paid ? 123_000 : 0;
                return SignedResponse(new PaymentLink
                {
                    Id = $"link-{orderCode}",
                    OrderCode = orderCode,
                    Amount = 123_000,
                    AmountPaid = amountPaid,
                    AmountRemaining = 123_000 - amountPaid,
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
