using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Data.Billing;
using Nexora.Data.Persistence;
using Nexora.Integrations.Payments;

namespace Nexora.IntegrationTests;

public sealed class VnpayBillingApiTests : IDisposable
{
    private const string HashSecret = "vnpay-test-hash-secret";
    private const string TmnCode = "NXTEST01";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly QueryDrHandler _queryDrHandler = new();
    private readonly NexoraApiFactory _factory;

    public VnpayBillingApiTests()
    {
        _factory = new NexoraApiFactory(
            new Dictionary<string, string?>
            {
                ["Billing:Payment:Provider"] = "vnpay",
                ["Billing:Vnpay:Environment"] = "Sandbox",
                ["Billing:Vnpay:TmnCode"] = TmnCode,
                ["Billing:Vnpay:HashSecret"] = HashSecret,
                ["Billing:Vnpay:ReturnUrl"] = "http://localhost:3000/payment/return"
            },
            services =>
            {
                services.RemoveAll<IPaymentProvider>();
                services.RemoveAll<IOptions<VnpayOptions>>();
                services.AddSingleton(Options.Create(new VnpayOptions
                {
                    Environment = "Sandbox",
                    TmnCode = TmnCode,
                    HashSecret = HashSecret,
                    ReturnUrl = "http://localhost:3000/payment/return"
                }));
                services.AddHttpClient<VnpayPaymentProvider>()
                    .ConfigurePrimaryHttpMessageHandler(() => _queryDrHandler);
                services.AddSingleton<IPaymentProvider>(provider => provider.GetRequiredService<VnpayPaymentProvider>());
            });
        _factory.InitializeDatabase();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task ValidVnpayIpnFulfillsOrderOnce()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 3);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "vnpay-ipn-success");

        using var first = await client.GetAsync(SignedIpnPath(checkout.OrderId, checkout.AmountMinor));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertRspAsync(first, "00", "Confirm Success");

        using var duplicate = await client.GetAsync(SignedIpnPath(checkout.OrderId, checkout.AmountMinor));
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        await AssertRspAsync(duplicate, "02", "Order already confirmed");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Subscriptions.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Entitlements.CountAsync(item => item.UserId == account.UserId));
        Assert.Equal(BillingValues.Fulfilled, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
    }

    [Fact]
    public async Task InvalidVnpaySignatureDoesNotFulfill()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "vnpay-ipn-bad-signature");

        using var response = await client.GetAsync(SignedIpnPath(checkout.OrderId, checkout.AmountMinor) + "bad");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertRspAsync(response, "97", "Invalid signature");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.PaymentEvents.Where(item => item.OrderId == checkout.OrderId).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId).ToListAsync());
    }

    [Fact]
    public async Task UnknownOrderAndWrongAmountMapToVnpayProtocolCodes()
    {
        using var client = _factory.CreateHttpsClient();
        using var unknown = await client.GetAsync(SignedIpnPath(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), 49_000));
        await AssertRspAsync(unknown, "01", "Order not found");

        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "vnpay-ipn-wrong-amount");
        using var wrongAmount = await client.GetAsync(SignedIpnPath(checkout.OrderId, checkout.AmountMinor + 1));
        await AssertRspAsync(wrongAmount, "04", "Invalid amount");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId).ToListAsync());
    }

    [Fact]
    public async Task UnsuccessfulVnpayPaymentStatusRecordsEventWithoutEntitlement()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 1);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "vnpay-ipn-failed-payment");

        using var response = await client.GetAsync(SignedIpnPath(checkout.OrderId, checkout.AmountMinor, responseCode: "00", transactionStatus: "02"));
        await AssertRspAsync(response, "00", "Confirm Success");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Empty(await db.Entitlements.Where(item => item.UserId == account.UserId).ToListAsync());
        Assert.Equal(BillingValues.Pending, (await db.Orders.SingleAsync(item => item.Id == checkout.OrderId)).Status);
    }

    [Fact]
    public async Task RefreshCheckoutUsesSignedVnpayQueryDrAndSharedFulfillment()
    {
        using var client = _factory.CreateHttpsClient();
        var account = await RegisterAsync(client);
        var price = await SeedPlanPriceAsync(interviewQuota: 2);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        var checkout = await CreateCheckoutAsync(client, price.Id, "vnpay-querydr-success");

        using var response = await client.PostAsync($"/api/v1/checkout-sessions/{checkout.OrderId}/refresh", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(BillingValues.Fulfilled, json.RootElement.GetProperty("data").GetProperty("status").GetString());
        Assert.Equal(1, _queryDrHandler.RequestCount);
        Assert.Equal(VnpayPaymentProvider.ToVnpayTxnRef(checkout.OrderId), _queryDrHandler.LastTxnRef);
        Assert.True(_queryDrHandler.LastRequestHadValidSignature);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        Assert.Equal(1, await db.PaymentEvents.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Subscriptions.CountAsync(item => item.OrderId == checkout.OrderId));
        Assert.Equal(1, await db.Entitlements.CountAsync(item => item.UserId == account.UserId));
    }

    private async Task<PlanPrice> SeedPlanPriceAsync(int interviewQuota)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexoraDbContext>();
        var now = DateTimeOffset.UtcNow;
        var plan = new Plan { Id = Guid.NewGuid(), Code = $"vnpay-test-{Guid.NewGuid():N}", Name = "VNPAY test plan", IsActive = true, CreatedAt = now };
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

    private static async Task<Account> RegisterAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"vnpay-{Guid.NewGuid():N}@example.test",
            password = "Strong!Pass123",
            displayName = "VNPAY candidate"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        return new Account(data.GetProperty("user").GetProperty("id").GetGuid(), data.GetProperty("accessToken").GetString()!);
    }

    private static async Task<CheckoutTestResponse> CreateCheckoutAsync(HttpClient client, Guid planPriceId, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/checkout-sessions") { Content = JsonContent.Create(new { planPriceId }) };
        request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = json.RootElement.GetProperty("data");
        Assert.Equal("vnpay", data.GetProperty("provider").GetString());
        Assert.StartsWith("https://sandbox.vnpayment.vn/paymentv2/vpcpay.html", data.GetProperty("checkoutUrl").GetString(), StringComparison.Ordinal);
        return new CheckoutTestResponse(data.GetProperty("orderId").GetGuid(), data.GetProperty("amountMinor").GetInt64());
    }

    private static string SignedIpnPath(Guid orderId, long amountMinor, string responseCode = "00", string transactionStatus = "00")
    {
        var txnRef = VnpayPaymentProvider.ToVnpayTxnRef(orderId);
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = VnpayPaymentProvider.ToVnpayAmount(amountMinor).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["vnp_BankCode"] = "NCB",
            ["vnp_OrderInfo"] = $"Nexora order {txnRef}",
            ["vnp_PayDate"] = "20260902081500",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_TmnCode"] = TmnCode,
            ["vnp_TransactionNo"] = "987654",
            ["vnp_TransactionStatus"] = transactionStatus,
            ["vnp_TxnRef"] = txnRef
        };
        query["vnp_SecureHash"] = VnpayPaymentProvider.SignSha512(VnpayPaymentProvider.BuildCanonicalQuery(query), HashSecret);
        return $"/api/v1/webhooks/payments/vnpay?{VnpayPaymentProvider.BuildCanonicalQuery(query)}";
    }

    private static async Task AssertRspAsync(HttpResponseMessage response, string code, string message)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("RspCode").GetString());
        Assert.Equal(message, json.RootElement.GetProperty("Message").GetString());
    }

    private sealed record Account(Guid UserId, string AccessToken);
    private sealed record CheckoutTestResponse(Guid OrderId, long AmountMinor);

    private sealed class QueryDrHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? LastTxnRef { get; private set; }
        public bool LastRequestHadValidSignature { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            LastTxnRef = root.GetProperty("vnp_TxnRef").GetString();
            var rawHash = string.Join('|',
                root.GetProperty("vnp_RequestId").GetString(),
                root.GetProperty("vnp_Version").GetString(),
                root.GetProperty("vnp_Command").GetString(),
                root.GetProperty("vnp_TmnCode").GetString(),
                LastTxnRef,
                root.GetProperty("vnp_TransactionDate").GetString(),
                root.GetProperty("vnp_CreateDate").GetString(),
                root.GetProperty("vnp_IpAddr").GetString(),
                root.GetProperty("vnp_OrderInfo").GetString());
            LastRequestHadValidSignature = string.Equals(
                root.GetProperty("vnp_SecureHash").GetString(),
                VnpayPaymentProvider.SignSha512(rawHash, HashSecret),
                StringComparison.Ordinal);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SignedQueryResponse(LastTxnRef!), Encoding.UTF8, "application/json")
            };
        }
    }

    private static string SignedQueryResponse(string txnRef, string responseCode = "00", string transactionStatus = "00")
    {
        var values = new Dictionary<string, object?>
        {
            ["vnp_ResponseId"] = "query-response-1",
            ["vnp_Command"] = "querydr",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_Message"] = "Success",
            ["vnp_TmnCode"] = TmnCode,
            ["vnp_TxnRef"] = txnRef,
            ["vnp_Amount"] = 12_300_000,
            ["vnp_BankCode"] = "NCB",
            ["vnp_PayDate"] = "20260902081500",
            ["vnp_TransactionNo"] = "987654",
            ["vnp_TransactionType"] = "01",
            ["vnp_TransactionStatus"] = transactionStatus,
            ["vnp_OrderInfo"] = $"Nexora order {txnRef}",
            ["vnp_PromotionCode"] = null,
            ["vnp_PromotionAmount"] = null
        };
        var raw = string.Join('|',
            values["vnp_ResponseId"],
            values["vnp_Command"],
            values["vnp_ResponseCode"],
            values["vnp_Message"],
            values["vnp_TmnCode"],
            values["vnp_TxnRef"],
            values["vnp_Amount"],
            values["vnp_BankCode"],
            values["vnp_PayDate"],
            values["vnp_TransactionNo"],
            values["vnp_TransactionType"],
            values["vnp_TransactionStatus"],
            values["vnp_OrderInfo"],
            string.Empty,
            string.Empty);
        values["vnp_SecureHash"] = VnpayPaymentProvider.SignSha512(raw, HashSecret);
        return JsonSerializer.Serialize(values);
    }
}
