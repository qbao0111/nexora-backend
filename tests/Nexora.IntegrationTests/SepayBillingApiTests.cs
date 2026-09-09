using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexora.Business.Billing;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;
using Nexora.Integrations.Payments;

namespace Nexora.IntegrationTests;

public sealed class SepayBillingApiTests : IDisposable
{
    private const string SecretKey = "sepay-test-secret";
    private readonly SepayQueryHandler _queryHandler = new();
    private readonly NexoraApiFactory _factory;

    public SepayBillingApiTests()
    {
        _factory = new NexoraApiFactory(new Dictionary<string, string?>
        {
            ["Billing:Payment:Provider"] = "sepay",
            ["Billing:Sepay:Environment"] = "Sandbox",
            ["Billing:Sepay:MerchantId"] = "MERCHANT_TEST",
            ["Billing:Sepay:SecretKey"] = SecretKey,
            ["Billing:Sepay:CheckoutUrl"] = "https://pay-sandbox.sepay.vn/v1/checkout/init",
            ["Billing:Sepay:ApiBaseUrl"] = "https://pgapi-sandbox.sepay.vn",
            ["Billing:Sepay:SuccessUrl"] = "https://frontend.example.test/payment/success",
            ["Billing:Sepay:ErrorUrl"] = "https://frontend.example.test/payment/error",
            ["Billing:Sepay:CancelUrl"] = "https://frontend.example.test/payment/cancel"
        }, services =>
        {
            services.RemoveAll<IPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(provider => provider.GetRequiredService<SepayPaymentProvider>());
            services.AddHttpClient<SepayPaymentProvider>().ConfigurePrimaryHttpMessageHandler(_ => _queryHandler);
        });
        _factory.InitializeDatabase();
    }

    [Fact]
    public async Task CheckoutReturnsOrderedPostActionAndPaidIpnFulfillsOnce()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "sepay-checkout");

        using var paid = await SendIpnAsync(client, checkout.OrderId, "ORDER_PAID", "CAPTURED", "APPROVED");
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.True(JsonDocument.Parse(await paid.Content.ReadAsStringAsync()).RootElement.GetProperty("success").GetBoolean());
        using var duplicate = await SendIpnAsync(client, checkout.OrderId, "ORDER_PAID", "CAPTURED", "APPROVED");
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Subscriptions.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Entitlements.CountAsync(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free"));
        Assert.Equal(BillingValues.Fulfilled, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
    }

    [Fact]
    public async Task ReusingCheckoutIdempotencyKeyReturnsEquivalentSignedAction()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);

        var first = await CreateCheckoutAsync(client, price.Id, "sepay-idempotent");
        var second = await CreateCheckoutAsync(client, price.Id, "sepay-idempotent");

        Assert.Equal(first.OrderId, second.OrderId);
        Assert.Equal(first.AmountMinor, second.AmountMinor);
        Assert.Equal(first.ActionFingerprint, second.ActionFingerprint);
    }

    [Fact]
    public async Task InvalidSecretUnknownInvoiceAndWrongAmountDoNotFulfill()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "sepay-invalid");

        using var wrongSecret = await SendIpnAsync(client, checkout.OrderId, "ORDER_PAID", "CAPTURED", "APPROVED", secret: "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode);
        using var unknown = await SendIpnAsync(client, checkout.OrderId, "ORDER_PAID", "CAPTURED", "APPROVED", invoice: "NX22222222222222222222222222222222");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var wrongAmount = await SendIpnAsync(client, checkout.OrderId, "ORDER_PAID", "CAPTURED", "APPROVED", amount: 1);
        Assert.Equal(HttpStatusCode.BadRequest, wrongAmount.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.PaymentEvents.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
        Assert.Equal(BillingValues.Pending, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
    }

    [Fact]
    public async Task VoidIpnMarksPendingOrderFailedWithoutEntitlement()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "sepay-void");
        using var response = await SendIpnAsync(client, checkout.OrderId, "TRANSACTION_VOID", "CANCELLED", "VOIDED");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(BillingValues.Failed, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
        Assert.Equal(1, await db.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Empty(await db.Subscriptions.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId && item.PlanCodeSnapshot != "free").ToListAsync());
    }

    [Theory]
    [InlineData("CAPTURED", "fulfilled")]
    [InlineData("AUTHENTICATION_NOT_NEEDED", "pending")]
    [InlineData("CANCELLED", "failed")]
    public async Task RefreshMapsSePayOrderStatus(string providerStatus, string expectedStatus)
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, $"sepay-refresh-{providerStatus}");
        _queryHandler.Status = providerStatus;

        using var response = await client.PostAsync($"/api/v1/checkout-sessions/{checkout.OrderId}/refresh", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedStatus, json.RootElement.GetProperty("data").GetProperty("status").GetString());
    }

    [Fact]
    public async Task RefreshProviderErrorIsSafeAndLeavesOrderPending()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "sepay-refresh-error");
        _queryHandler.ResponseStatus = HttpStatusCode.ServiceUnavailable;

        using var response = await client.PostAsync($"/api/v1/checkout-sessions/{checkout.OrderId}/refresh", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PAYMENT_PROVIDER_UNAVAILABLE", json.RootElement.GetProperty("error").GetProperty("code").GetString());
        using var scope = _factory.Services.CreateScope();
        Assert.Equal(BillingValues.Pending, (await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
    }

    private async Task<PlanPrice> SeedPlanPriceAsync(int quota)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"sepay-{Guid.NewGuid():N}", Name = "SePay test plan", IsActive = true, CreatedAt = now };
        var price = new PlanPrice { Id = Guid.NewGuid(), PlanId = plan.Id, AmountMinor = 123_000, Currency = "VND", DurationDays = 14, InterviewQuota = quota, IsActive = true, CreatedAt = now };
        db.AddRange(plan, price);
        await db.SaveChangesAsync();
        return price;
    }

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        var email = $"sepay-{Guid.NewGuid():N}@example.test";
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { email, password = "Strong!Pass123", displayName = "SePay candidate" });
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
        Assert.Equal("sepay", data.GetProperty("provider").GetString());
        var action = data.GetProperty("checkout");
        Assert.Equal("POST", action.GetProperty("method").GetString());
        Assert.Equal("https://pay-sandbox.sepay.vn/v1/checkout/init", action.GetProperty("url").GetString());
        var formFields = action.GetProperty("fields").EnumerateArray().Select(item =>
            (Name: item.GetProperty("name").GetString()!, Value: item.GetProperty("value").GetString()!)).ToArray();
        var fields = formFields.Select(field => $"{field.Name}={field.Value}").ToArray();
        Assert.Equal(
            ["order_amount", "merchant", "currency", "operation", "order_description", "order_invoice_number", "success_url", "error_url", "cancel_url", "signature"],
            formFields.Select(field => field.Name).ToArray());
        Assert.Equal("https://frontend.example.test/payment/success", formFields[6].Value);
        Assert.Equal("https://frontend.example.test/payment/error", formFields[7].Value);
        Assert.Equal("https://frontend.example.test/payment/cancel", formFields[8].Value);
        Assert.DoesNotContain(formFields, field => string.Equals(field.Name, "secret_key", StringComparison.OrdinalIgnoreCase));
        return new CheckoutResponse(data.GetProperty("orderId").GetGuid(), data.GetProperty("amountMinor").GetInt64(), string.Join("\n", fields));
    }

    private async Task<HttpResponseMessage> SendIpnAsync(HttpClient client, Guid orderId, string type, string orderStatus, string transactionStatus, string? secret = null, string? invoice = null, long? amount = null)
    {
        using var scope = _factory.Services.CreateScope();
        var order = await scope.ServiceProvider.GetRequiredService<NexoraDbContext>().Orders.AsNoTracking().SingleAsync(item => item.Id == orderId);
        var actualInvoice = invoice ?? order.ProviderTransactionId;
        var actualAmount = amount ?? order.AmountMinor;
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            notification_type = type,
            order = new { order_id = "SEPAY-ORDER-1", order_status = orderStatus, order_currency = "VND", order_amount = $"{actualAmount}.00", order_invoice_number = actualInvoice },
            transaction = new { id = "1", transaction_id = "SEPAY-TXN-1", transaction_status = transactionStatus, transaction_amount = actualAmount.ToString(CultureInfo.InvariantCulture), transaction_currency = "VND" }
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/payments/sepay") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("X-Secret-Key", secret ?? SecretKey);
        return await client.SendAsync(request);
    }

    public void Dispose() => _factory.Dispose();

    private sealed record Account(Guid UserId, string AccessToken);
    private sealed record CheckoutResponse(Guid OrderId, long AmountMinor, string ActionFingerprint);

    private sealed class SepayQueryHandler : HttpMessageHandler
    {
        public string Status { get; set; } = "AUTHENTICATION_NOT_NEEDED";
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var invoice = request.RequestUri?.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(parts => parts.Length == 2 && parts[0] == "q")
                .Select(parts => WebUtility.UrlDecode(parts[1]))
                .FirstOrDefault() ?? string.Empty;
            var response = new HttpResponseMessage(ResponseStatus)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    data = new[] { new { order_id = "SEPAY-ORDER-1", order_status = Status, order_currency = "VND", order_amount = "123000.00", order_invoice_number = invoice } }
                }), Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
