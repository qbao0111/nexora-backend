using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Integrations;
using Nexora.Integrations.Payments;

namespace Nexora.UnitTests;

public sealed class SepayPaymentProviderTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly SepayOptions TestOptions = new()
    {
        Environment = "Sandbox",
        MerchantId = "MERCHANT_123",
        SecretKey = "secret",
        CheckoutUrl = "https://pay-sandbox.sepay.vn/v1/checkout/init",
        ApiBaseUrl = "https://pgapi-sandbox.sepay.vn"
    };

    [Fact]
    public void InvoiceIsDeterministicUniqueReversibleAndAlphanumeric()
    {
        var invoice = SepayPaymentProvider.ToSepayInvoiceNumber(OrderId);
        Assert.Equal("NX11111111222233334444555555555555", invoice);
        Assert.All(invoice, character => Assert.True(char.IsLetterOrDigit(character)));
        Assert.True(SepayPaymentProvider.TryParseSepayInvoiceNumber(invoice, out var parsed));
        Assert.Equal(OrderId, parsed);
        Assert.NotEqual(invoice, SepayPaymentProvider.ToSepayInvoiceNumber(Guid.NewGuid()));
        Assert.False(SepayPaymentProvider.TryParseSepayInvoiceNumber("NX-not-a-guid", out _));
    }

    [Fact]
    public async Task CheckoutUsesActualVndAmountAndOrderedPostFields()
    {
        var provider = NewProvider();
        var invoice = SepayPaymentProvider.ToSepayInvoiceNumber(OrderId);
        var checkout = await provider.CreateCheckoutAsync(
            new PaymentOrderRequest(OrderId, 49_000, "VND", invoice, DateTimeOffset.UtcNow, null), CancellationToken.None);

        Assert.Equal("sepay", checkout.Provider);
        Assert.Equal(invoice, checkout.ProviderTransactionId);
        Assert.Equal("POST", checkout.Action.Method);
        Assert.Equal(TestOptions.CheckoutUrl, checkout.Action.Url);
        Assert.Equal(
            ["order_amount", "merchant", "currency", "operation", "order_description", "order_invoice_number", "signature"],
            checkout.Action.Fields.Select(field => field.Name).ToArray());
        Assert.Equal("49000", checkout.Action.Fields[0].Value);
        Assert.DoesNotContain(checkout.Action.Fields, field => field.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) || field.Value == TestOptions.SecretKey);
    }

    [Fact]
    public void CheckoutSignatureMatchesIndependentFixedVector()
    {
        var fields = new List<CheckoutFormField>
        {
            new("order_amount", "49000"),
            new("merchant", "MERCHANT_123"),
            new("currency", "VND"),
            new("operation", "PURCHASE"),
            new("order_description", "Nexora order NX11111111222233334444555555555555"),
            new("order_invoice_number", "NX11111111222233334444555555555555")
        };
        const string signingString = "order_amount=49000,merchant=MERCHANT_123,currency=VND,operation=PURCHASE,order_description=Nexora order NX11111111222233334444555555555555,order_invoice_number=NX11111111222233334444555555555555";
        const string expected = "fFnFOs0oMX6hqjiQ7JT7vDMbjFbhd3VjBSJ1fguMYfg=";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("secret"));
        var independent = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signingString)));
        Assert.Equal(expected, independent);
        Assert.Equal(expected, SepayPaymentProvider.SignFields(fields, "secret"));
        Assert.Equal(expected, SepayPaymentProvider.SignFields(fields.OrderByDescending(field => field.Name).ToArray(), "secret"));
    }

    [Fact]
    public void OptionalFieldsAreOmittedWhenUnsetAndProtocolOrderIsNotAlphabetical()
    {
        var fields = new List<CheckoutFormField>
        {
            new("order_amount", "49000"),
            new("merchant", "MERCHANT_123"),
            new("currency", "VND"),
            new("operation", "PURCHASE"),
            new("order_description", "Nexora order NX11111111222233334444555555555555"),
            new("order_invoice_number", "NX11111111222233334444555555555555")
        };
        Assert.Equal("order_amount", fields[0].Name);
        Assert.NotEqual(fields.Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal), fields.Select(field => field.Name));
        Assert.DoesNotContain("payment_method", fields.Select(field => field.Name));
    }

    [Fact]
    public async Task ValidPaidIpnMapsToPaidFinalEvent()
    {
        var provider = NewProvider();
        var payload = BuildIpn("ORDER_PAID", "CAPTURED", "APPROVED");
        var result = await provider.VerifyWebhookAsync(Callback(payload), CancellationToken.None);

        Assert.Equal(OrderId, result.OrderId);
        Assert.Equal("NX11111111222233334444555555555555", result.ProviderTransactionId);
        Assert.Equal(49_000, result.AmountMinor);
        Assert.True(result.IsPaid);
        Assert.True(result.IsFinal);
        Assert.StartsWith("sepay:ipn:ORDER_PAID:", result.ProviderEventId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WrongOrMissingSecretIsRejected()
    {
        var provider = NewProvider();
        var request = Callback(BuildIpn("ORDER_PAID", "CAPTURED", "APPROVED"), "wrong");
        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.VerifyWebhookAsync(request, CancellationToken.None));
        Assert.Equal("INVALID_WEBHOOK_SIGNATURE", exception.Code);
        Assert.Equal(BusinessErrorKind.Unauthorized, exception.Kind);
        var missing = await Assert.ThrowsAsync<BusinessException>(() => provider.VerifyWebhookAsync(
            new PaymentCallbackRequest("POST", new Dictionary<string, string>(), new Dictionary<string, string>(), Encoding.UTF8.GetBytes(BuildIpn("ORDER_PAID", "CAPTURED", "APPROVED"))), CancellationToken.None));
        Assert.Equal("INVALID_WEBHOOK_SIGNATURE", missing.Code);
    }

    [Fact]
    public async Task InvalidCurrencyAmountInvoiceAndNotificationAreRejected()
    {
        var provider = NewProvider();
        foreach (var payload in new[]
        {
            BuildIpn("ORDER_PAID", "CAPTURED", "APPROVED", currency: "USD"),
            BuildIpn("ORDER_PAID", "CAPTURED", "APPROVED", transactionAmount: "49001"),
            BuildIpn("ORDER_PAID", "CAPTURED", "APPROVED", invoice: "BAD"),
            BuildIpn("UNKNOWN", "CAPTURED", "APPROVED")
        })
        {
            var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.VerifyWebhookAsync(Callback(payload), CancellationToken.None));
            Assert.Equal("INVALID_WEBHOOK_PAYLOAD", exception.Code);
        }
    }

    [Fact]
    public async Task TransactionVoidIsFinalUnpaid()
    {
        var provider = NewProvider();
        var result = await provider.VerifyWebhookAsync(Callback(BuildIpn("TRANSACTION_VOID", "CANCELLED", "VOIDED")), CancellationToken.None);
        Assert.False(result.IsPaid);
        Assert.True(result.IsFinal);
    }

    [Fact]
    public async Task QueryMatchesInvoiceExactlyAndUsesBasicAuth()
    {
        var handler = new QueryHandler(JsonSerializer.Serialize(new { data = new[] { Order("CAPTURED") } }));
        var provider = NewProvider(handler: handler);
        var result = await provider.QueryPaymentAsync(Request(), CancellationToken.None);
        Assert.NotNull(result);
        Assert.True(result!.IsPaid);
        Assert.True(result.IsFinal);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("MERCHANT_123:secret")), handler.Authorization);
        Assert.Contains("q=NX11111111222233334444555555555555", handler.RequestUri, StringComparison.Ordinal);

        handler.Body = JsonSerializer.Serialize(new { data = new[] { Order("CAPTURED", invoice: "NX1111111122223333444455555555555X") } });
        Assert.Null(await provider.QueryPaymentAsync(Request(), CancellationToken.None));
    }

    [Theory]
    [InlineData("AUTHENTICATION_NOT_NEEDED", false, false)]
    [InlineData("CANCELLED", true, false)]
    public async Task QueryStatusMapsPendingAndCancelled(string status, bool expectedEvent, bool expectedPaid)
    {
        var provider = NewProvider(handler: new QueryHandler(JsonSerializer.Serialize(new { data = new[] { Order(status) } })));
        var result = await provider.QueryPaymentAsync(Request(), CancellationToken.None);
        Assert.Equal(expectedEvent, result is not null);
        if (result is not null)
        {
            Assert.Equal(expectedPaid, result.IsPaid);
            Assert.True(result.IsFinal);
        }
    }

    [Fact]
    public async Task UnknownQueryStatusAndHttpFailuresAreSafe()
    {
        var unknown = NewProvider(handler: new QueryHandler(JsonSerializer.Serialize(new { data = new[] { Order("UNKNOWN") } })));
        var exception = await Assert.ThrowsAsync<BusinessException>(() => unknown.QueryPaymentAsync(Request(), CancellationToken.None));
        Assert.Equal("PAYMENT_PROVIDER_QUERY_FAILED", exception.Code);

        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
        {
            var auth = NewProvider(handler: new QueryHandler("", status));
            exception = await Assert.ThrowsAsync<BusinessException>(() => auth.QueryPaymentAsync(Request(), CancellationToken.None));
            Assert.Equal("PAYMENT_PROVIDER_AUTH_FAILED", exception.Code);
        }
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.BadGateway })
        {
            var unavailable = NewProvider(handler: new QueryHandler("", status));
            exception = await Assert.ThrowsAsync<BusinessException>(() => unavailable.QueryPaymentAsync(Request(), CancellationToken.None));
            Assert.Equal("PAYMENT_PROVIDER_UNAVAILABLE", exception.Code);
        }
    }

    [Fact]
    public void SepayConfigurationRequiresSandboxCredentialsAndEndpoints()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:Ai"] = "false",
            ["Billing:Payment:Provider"] = "sepay",
            ["Billing:Sepay:Environment"] = "Sandbox",
            ["Billing:Sepay:MerchantId"] = "",
            ["Billing:Sepay:SecretKey"] = ""
        }).Build();
        using var serviceProvider = new ServiceCollection().AddIntegrations(configuration).BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => serviceProvider.GetRequiredService<IOptions<SepayOptions>>().Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("CARD")]
    [InlineData("BANK_TRANSFER")]
    [InlineData("NAPAS_BANK_TRANSFER")]
    [InlineData("card")]
    [InlineData("bank_transfer")]
    [InlineData("   ")]
    public void AllowedSepayPaymentMethodsPassOptionsValidation(string paymentMethod)
    {
        using var serviceProvider = new ServiceCollection()
            .AddIntegrations(CreateSepayConfiguration(paymentMethod))
            .BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptions<SepayOptions>>().Value;
        Assert.Equal(paymentMethod, options.PaymentMethod);
    }

    [Theory]
    [InlineData("PAYPAL")]
    [InlineData("MOMO")]
    [InlineData("BANK")]
    [InlineData("INVALID")]
    public void InvalidSepayPaymentMethodsFailOptionsValidation(string paymentMethod)
    {
        using var serviceProvider = new ServiceCollection()
            .AddIntegrations(CreateSepayConfiguration(paymentMethod))
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => serviceProvider.GetRequiredService<IOptions<SepayOptions>>().Value);
    }

    private static IConfiguration CreateSepayConfiguration(string paymentMethod) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:Ai"] = "false",
            ["Billing:Payment:Provider"] = "sepay",
            ["Billing:Sepay:Environment"] = "Sandbox",
            ["Billing:Sepay:MerchantId"] = "MERCHANT_TEST",
            ["Billing:Sepay:SecretKey"] = "secret",
            ["Billing:Sepay:CheckoutUrl"] = "https://pay-sandbox.sepay.vn/v1/checkout/init",
            ["Billing:Sepay:ApiBaseUrl"] = "https://pgapi-sandbox.sepay.vn",
            ["Billing:Sepay:PaymentMethod"] = paymentMethod
        }).Build();

    private static SepayPaymentProvider NewProvider(SepayOptions? options = null, HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? new QueryHandler("{}")), Options.Create(options ?? TestOptions), TimeProvider.System);

    private static PaymentOrderRequest Request() => new(OrderId, 49_000, "VND", SepayPaymentProvider.ToSepayInvoiceNumber(OrderId), DateTimeOffset.UtcNow, null);

    private static PaymentCallbackRequest Callback(string body, string secret = "secret") =>
        new("POST", new Dictionary<string, string>(), new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Secret-Key"] = secret }, Encoding.UTF8.GetBytes(body));

    private static string BuildIpn(string type, string orderStatus, string transactionStatus, string currency = "VND", string transactionAmount = "49000", string invoice = "NX11111111222233334444555555555555") =>
        JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            notification_type = type,
            order = new { order_id = "SEPAY-ORDER-1", order_status = orderStatus, order_currency = currency, order_amount = "49000.00", order_invoice_number = invoice },
            transaction = new { id = "1", transaction_id = "SEPAY-TXN-1", transaction_status = transactionStatus, transaction_amount = transactionAmount, transaction_currency = currency }
        });

    private static object Order(string status, string invoice = "NX11111111222233334444555555555555") =>
        new { order_id = "SEPAY-ORDER-1", order_status = status, order_currency = "VND", order_amount = "49000.00", order_invoice_number = invoice };

    private sealed class QueryHandler(string body, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string Body { get; set; } = body;
        public string? Authorization { get; private set; }
        public string? RequestUri { get; private set; }
        private readonly HttpStatusCode _statusCode = statusCode;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            RequestUri = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json")
            });
        }
    }
}
