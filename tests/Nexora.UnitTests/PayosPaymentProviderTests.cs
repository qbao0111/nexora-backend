using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Integrations;
using Nexora.Integrations.Payments;
using PayOS.Crypto;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;

namespace Nexora.UnitTests;

public sealed class PayosPaymentProviderTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const string ClientId = "payos-test-client";
    private const string ApiKey = "payos-test-api-key";
    private const string ChecksumKey = "payos-test-checksum-key";
    private const string ProviderTransactionId = "1234567890123456";

    [Fact]
    public void ValidPayosConfigurationPassesOptionsValidation()
    {
        using var serviceProvider = new ServiceCollection().AddIntegrations(CreatePayosConfiguration()).BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<PayosOptions>>().Value;

        Assert.Equal(ClientId, options.ClientId);
        Assert.Equal("https://frontend.example.test/payment/success", options.ReturnUrl);
        Assert.Equal(15, options.TimeoutSeconds);
    }

    [Fact]
    public void LoopbackHttpCallbackUrlsPassLocalPayosOptionsValidation()
    {
        var values = PayosConfigurationValues();
        values["Billing:Payos:ReturnUrl"] = "http://localhost:3000/payment/success";
        values["Billing:Payos:CancelUrl"] = "http://127.0.0.1:3000/payment/cancel";
        using var serviceProvider = new ServiceCollection()
            .AddIntegrations(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider();

        var options = serviceProvider.GetRequiredService<IOptions<PayosOptions>>().Value;
        Assert.Equal("http://localhost:3000/payment/success", options.ReturnUrl);
    }

    [Theory]
    [InlineData("Billing:Payos:ClientId")]
    [InlineData("Billing:Payos:ApiKey")]
    [InlineData("Billing:Payos:ChecksumKey")]
    public void MissingPayosSecretConfigurationFailsOptionsValidation(string missingKey)
    {
        var values = PayosConfigurationValues();
        values[missingKey] = " ";
        using var serviceProvider = new ServiceCollection()
            .AddIntegrations(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => serviceProvider.GetRequiredService<IOptions<PayosOptions>>().Value);
    }

    [Theory]
    [InlineData("", "https://frontend.example.test/payment/cancel")]
    [InlineData("http://frontend.example.test/payment/success", "https://frontend.example.test/payment/cancel")]
    [InlineData("https://user:pass@frontend.example.test/payment/success", "https://frontend.example.test/payment/cancel")]
    [InlineData("https://frontend.example.test/payment/success#fragment", "https://frontend.example.test/payment/cancel")]
    [InlineData("https://frontend.example.test/payment/success", "http://frontend.example.test/payment/cancel")]
    [InlineData("https://frontend.example.test/payment/success", " ")]
    public void InvalidPayosCallbackUrlsFailOptionsValidation(string returnUrl, string cancelUrl)
    {
        var values = PayosConfigurationValues();
        values["Billing:Payos:ReturnUrl"] = returnUrl;
        values["Billing:Payos:CancelUrl"] = cancelUrl;
        using var serviceProvider = new ServiceCollection()
            .AddIntegrations(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => serviceProvider.GetRequiredService<IOptions<PayosOptions>>().Value);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("61")]
    public void InvalidPayosTimeoutFailsOptionsValidation(string timeout)
    {
        var values = PayosConfigurationValues();
        values["Billing:Payos:TimeoutSeconds"] = timeout;
        using var serviceProvider = new ServiceCollection()
            .AddIntegrations(new ConfigurationBuilder().AddInMemoryCollection(values).Build())
            .BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => serviceProvider.GetRequiredService<IOptions<PayosOptions>>().Value);
    }

    [Fact]
    public void NumericProviderTransactionIdFitsPayosOrderCodeLimit()
    {
        using var provider = NewProvider();
        var identifiers = Enumerable.Range(0, 32).Select(_ => provider.CreateProviderTransactionId(Guid.NewGuid())).ToArray();

        Assert.Equal(identifiers.Length, identifiers.Distinct(StringComparer.Ordinal).Count());
        foreach (var identifier in identifiers)
        {
            Assert.True(long.TryParse(identifier, NumberStyles.None, CultureInfo.InvariantCulture, out var orderCode));
            Assert.InRange(orderCode, 1L, 9_007_199_254_740_991L);
        }
    }

    [Fact]
    public async Task CreateCheckoutUsesPersistedProviderTransactionIdAndReturnsRedirectAction()
    {
        var handler = new PayosHandler(_ => SignedResponse(CreateLink()));
        using var provider = NewProvider(handler: handler);
        var request = Request();

        var first = await provider.CreateCheckoutAsync(request, CancellationToken.None);
        var second = await provider.CreateCheckoutAsync(request, CancellationToken.None);

        Assert.Equal("payos", first.Provider);
        Assert.Equal(ProviderTransactionId, first.ProviderTransactionId);
        Assert.Equal("GET", first.Action.Method);
        Assert.Equal("https://pay.payos.vn/web/test-payment-link", first.Action.Url);
        Assert.Empty(first.Action.Fields);
        Assert.Equal(first.Action, second.Action);
        Assert.Equal(2, handler.RequestBodies.Count);
        foreach (var body in handler.RequestBodies)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal(long.Parse(ProviderTransactionId, CultureInfo.InvariantCulture), json.RootElement.GetProperty("orderCode").GetInt64());
            Assert.Equal(49_000, json.RootElement.GetProperty("amount").GetInt64());
            Assert.Equal("NEXORA BASIC", json.RootElement.GetProperty("description").GetString());
            Assert.Equal(TestOptions.ReturnUrl, json.RootElement.GetProperty("returnUrl").GetString());
            Assert.Equal(TestOptions.CancelUrl, json.RootElement.GetProperty("cancelUrl").GetString());
            Assert.DoesNotContain(ChecksumKey, body, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("basic", "NEXORA BASIC")]
    [InlineData("weekly", "NEXORA PLUS")]
    [InlineData("pro", "NEXORA PRO")]
    public async Task CreateCheckoutUsesPlanSpecificPaymentDescription(string planCode, string expectedDescription)
    {
        var handler = new PayosHandler(_ => SignedResponse(CreateLink()));
        using var provider = NewProvider(handler: handler);

        await provider.CreateCheckoutAsync(Request(planCode), CancellationToken.None);

        using var json = JsonDocument.Parse(Assert.Single(handler.RequestBodies));
        Assert.Equal(expectedDescription, json.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task CreateCheckoutMapsUpstreamFailureWithoutLeakingDetails()
    {
        var handler = new PayosHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("provider unavailable", Encoding.UTF8, "application/json")
        });
        using var provider = NewProvider(handler: handler);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.CreateCheckoutAsync(Request(), CancellationToken.None));
        Assert.Equal("PAYMENT_PROVIDER_UNAVAILABLE", exception.Code);
        Assert.DoesNotContain("provider unavailable", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateCheckoutRejectsInvalidProviderResponse()
    {
        var handler = new PayosHandler(_ => SignedResponse(CreateLink(amount: 49_001)));
        using var provider = NewProvider(handler: handler);

        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.CreateCheckoutAsync(Request(), CancellationToken.None));
        Assert.Equal("PAYMENT_PROVIDER_INVALID_RESPONSE", exception.Code);
    }

    [Theory]
    [InlineData(PaymentLinkStatus.Pending, 0L, false, false)]
    [InlineData(PaymentLinkStatus.Processing, 0L, false, false)]
    [InlineData(PaymentLinkStatus.Paid, 49_000L, true, true)]
    [InlineData(PaymentLinkStatus.Cancelled, 0L, true, false)]
    [InlineData(PaymentLinkStatus.Underpaid, 10_000L, false, false)]
    [InlineData(PaymentLinkStatus.Expired, 0L, true, false)]
    public async Task QueryMapsPayosStatuses(PaymentLinkStatus status, long amountPaid, bool hasEvent, bool isPaid)
    {
        var handler = new PayosHandler(_ => SignedResponse(PaymentLink(status, amountPaid: amountPaid)));
        using var provider = NewProvider(handler: handler);

        var result = await provider.QueryPaymentAsync(Request(), CancellationToken.None);

        Assert.Equal(hasEvent, result is not null);
        if (result is not null)
        {
            Assert.Equal(isPaid, result.IsPaid);
            Assert.True(result.IsFinal);
            Assert.Equal(ProviderTransactionId, result.ProviderTransactionId);
        }
    }

    [Fact]
    public async Task QueryRejectsWrongAmountAndInvalidProviderResponse()
    {
        using (var wrongAmount = NewProvider(handler: new PayosHandler(_ => SignedResponse(PaymentLink(PaymentLinkStatus.Paid, amount: 49_001, amountPaid: 49_001)))))
        {
            var exception = await Assert.ThrowsAsync<BusinessException>(() => wrongAmount.QueryPaymentAsync(Request(), CancellationToken.None));
            Assert.Equal("PAYMENT_AMOUNT_MISMATCH", exception.Code);
        }

        using var invalid = NewProvider(handler: new PayosHandler(_ => SignedResponse(PaymentLink(PaymentLinkStatus.Pending, id: ""))));
        var invalidException = await Assert.ThrowsAsync<BusinessException>(() => invalid.QueryPaymentAsync(Request(), CancellationToken.None));
        Assert.Equal("PAYMENT_PROVIDER_INVALID_RESPONSE", invalidException.Code);
    }

    [Fact]
    public async Task ValidWebhookIsCryptographicallyVerifiedAndHasStableDuplicateIdentity()
    {
        var handler = new PayosHandler(_ => SignedResponse(PaymentLink(PaymentLinkStatus.Paid, amountPaid: 49_000)));
        using var provider = NewProvider(handler: handler);
        var payload = BuildWebhook();

        var first = await provider.VerifyWebhookAsync(Callback(payload), CancellationToken.None);
        var second = await provider.VerifyWebhookAsync(Callback(payload), CancellationToken.None);

        Assert.Null(first.OrderId);
        Assert.Equal(ProviderTransactionId, first.ProviderTransactionId);
        Assert.Equal(49_000, first.AmountMinor);
        Assert.Equal("VND", first.Currency);
        Assert.True(first.IsPaid);
        Assert.True(first.IsFinal);
        Assert.False(first.IsVerificationProbe);
        Assert.Equal(first.ProviderEventId, second.ProviderEventId);
        Assert.Equal(2, handler.RequestPaths.Count(path => path == "/v2/payment-requests/1234567890123456"));
    }

    [Fact]
    public async Task WebhookEventIdentityRemainsStableWhenPaymentLinkReconciliationChanges()
    {
        var status = PaymentLinkStatus.Underpaid;
        var amountPaid = 10_000L;
        var handler = new PayosHandler(_ => SignedResponse(PaymentLink(status, amountPaid: amountPaid)));
        using var provider = NewProvider(handler: handler);
        var payload = BuildWebhook(amount: 10_000, reference: "PAYOS-REFERENCE-SPLIT");

        var underpaid = await provider.VerifyWebhookAsync(Callback(payload), CancellationToken.None);
        status = PaymentLinkStatus.Paid;
        amountPaid = 49_000;
        var paid = await provider.VerifyWebhookAsync(Callback(payload), CancellationToken.None);

        Assert.False(underpaid.IsPaid);
        Assert.True(paid.IsPaid);
        Assert.Equal(49_000, paid.AmountMinor);
        Assert.Equal(underpaid.ProviderEventId, paid.ProviderEventId);
    }

    [Theory]
    [InlineData(PaymentLinkStatus.Pending, 0L)]
    [InlineData(PaymentLinkStatus.Processing, 0L)]
    [InlineData(PaymentLinkStatus.Underpaid, 10_000L)]
    public async Task ValidNonFinalWebhookReconcilesStatusWithoutMarkingPaymentPaid(PaymentLinkStatus status, long amountPaid)
    {
        var handler = new PayosHandler(_ => SignedResponse(PaymentLink(status, amountPaid: amountPaid)));
        using var provider = NewProvider(handler: handler);

        var result = await provider.VerifyWebhookAsync(
            Callback(BuildWebhook(amount: amountPaid == 0 ? 49_000 : amountPaid)),
            CancellationToken.None);

        Assert.False(result.IsPaid);
        Assert.False(result.IsFinal);
        Assert.Equal(49_000, result.AmountMinor);
        Assert.Single(handler.RequestPaths, path => path == "/v2/payment-requests/1234567890123456");
    }

    [Fact]
    public async Task PaidStatusWithPartialTotalDoesNotProducePaidEvent()
    {
        var handler = new PayosHandler(_ => SignedResponse(PaymentLink(PaymentLinkStatus.Paid, amountPaid: 10_000)));
        using var provider = NewProvider(handler: handler);

        var result = await provider.VerifyWebhookAsync(Callback(BuildWebhook(amount: 10_000)), CancellationToken.None);

        Assert.False(result.IsPaid);
        Assert.False(result.IsFinal);
    }

    [Fact]
    public async Task InvalidSignatureMalformedPayloadAndWrongAmountAreRejected()
    {
        using var provider = NewProvider();

        var invalidSignature = await Assert.ThrowsAsync<BusinessException>(() => provider.VerifyWebhookAsync(Callback(BuildWebhook(signature: "invalid")), CancellationToken.None));
        Assert.Equal("INVALID_WEBHOOK_SIGNATURE", invalidSignature.Code);

        var malformed = await Assert.ThrowsAsync<BusinessException>(() => provider.VerifyWebhookAsync(Callback(Encoding.UTF8.GetBytes("{")), CancellationToken.None));
        Assert.Equal("INVALID_WEBHOOK_PAYLOAD", malformed.Code);

        var wrongAmount = await Assert.ThrowsAsync<BusinessException>(() => provider.VerifyWebhookAsync(Callback(BuildWebhook(amount: 0)), CancellationToken.None));
        Assert.Equal("INVALID_WEBHOOK_PAYLOAD", wrongAmount.Code);
    }

    [Fact]
    public async Task VerifiedDashboardProbeIsAcknowledgedWithoutBeingTreatedAsAnOrder()
    {
        var handler = new PayosHandler(_ => throw new InvalidOperationException("The dashboard verification probe must not query payment-link status."));
        using var provider = NewProvider(handler: handler);
        var result = await provider.VerifyWebhookAsync(Callback(BuildDashboardProbe()), CancellationToken.None);

        Assert.True(result.IsVerificationProbe);
        Assert.Null(result.OrderId);
        Assert.Equal("123", result.ProviderTransactionId);
        Assert.Empty(handler.RequestPaths);
    }

    [Fact]
    public async Task InvalidSignatureIsRejectedBeforeDashboardProbeRecognition()
    {
        using var provider = NewProvider();

        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.VerifyWebhookAsync(Callback(BuildDashboardProbe(signature: "invalid")), CancellationToken.None));

        Assert.Equal("INVALID_WEBHOOK_SIGNATURE", exception.Code);
    }

    private static IConfiguration CreatePayosConfiguration() =>
        new ConfigurationBuilder().AddInMemoryCollection(PayosConfigurationValues()).Build();

    private static Dictionary<string, string?> PayosConfigurationValues() => new()
    {
        ["Features:Ai"] = "false",
        ["Billing:Payment:Provider"] = "payos",
        ["Billing:Payos:ClientId"] = ClientId,
        ["Billing:Payos:ApiKey"] = ApiKey,
        ["Billing:Payos:ChecksumKey"] = ChecksumKey,
        ["Billing:Payos:ReturnUrl"] = TestOptions.ReturnUrl,
        ["Billing:Payos:CancelUrl"] = TestOptions.CancelUrl,
        ["Billing:Payos:TimeoutSeconds"] = "15"
    };

    private static PayosOptions TestOptions => new()
    {
        ClientId = ClientId,
        ApiKey = ApiKey,
        ChecksumKey = ChecksumKey,
        ReturnUrl = "https://frontend.example.test/payment/success",
        CancelUrl = "https://frontend.example.test/payment/cancel",
        TimeoutSeconds = 15
    };

    private static PayosPaymentProvider NewProvider(PayosOptions? options = null, HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? new PayosHandler(_ => SignedResponse(PaymentLink(PaymentLinkStatus.Pending)))), Options.Create(options ?? TestOptions), TimeProvider.System);

    private static PaymentOrderRequest Request(string planCode = "basic") =>
        new(OrderId, 49_000, "VND", ProviderTransactionId, DateTimeOffset.UtcNow, null, planCode);

    private static PaymentCallbackRequest Callback(byte[] body) =>
        new("POST", new Dictionary<string, string>(), new Dictionary<string, string>(), body);

    private static byte[] BuildWebhook(
        long? orderCode = null,
        long amount = 49_000,
        string? signature = null,
        string reference = "PAYOS-REFERENCE-1")
    {
        var data = new WebhookData
        {
            OrderCode = orderCode ?? long.Parse(ProviderTransactionId, CultureInfo.InvariantCulture),
            Amount = amount,
            Description = "Nexora",
            AccountNumber = string.Empty,
            Reference = reference,
            TransactionDateTime = "2026-09-18 10:30:00",
            Currency = "VND",
            PaymentLinkId = "test-payment-link",
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

    private static byte[] BuildDashboardProbe(string? signature = null)
    {
        var data = new WebhookData
        {
            OrderCode = 123,
            Code = "probe"
        };
        var webhook = new Webhook
        {
            Code = "probe",
            Success = false,
            Data = data,
            Signature = signature ?? new CryptoProvider().CreateSignatureFromObject(data, ChecksumKey)!
        };
        return JsonSerializer.SerializeToUtf8Bytes(webhook);
    }

    private static CreatePaymentLinkResponse CreateLink(long amount = 49_000) => new()
    {
        OrderCode = long.Parse(ProviderTransactionId, CultureInfo.InvariantCulture),
        Amount = amount,
        Currency = "VND",
        PaymentLinkId = "test-payment-link",
        Status = PaymentLinkStatus.Pending,
        CheckoutUrl = "https://pay.payos.vn/web/test-payment-link"
    };

    private static PaymentLink PaymentLink(PaymentLinkStatus status, long amount = 49_000, long amountPaid = 0, string id = "test-payment-link") => new()
    {
        Id = id,
        OrderCode = long.Parse(ProviderTransactionId, CultureInfo.InvariantCulture),
        Amount = amount,
        AmountPaid = amountPaid,
        AmountRemaining = Math.Max(0, amount - amountPaid),
        Status = status,
        CreatedAt = "2026-09-18T03:30:00.000Z",
        Transactions = []
    };

    private static HttpResponseMessage SignedResponse<T>(T data)
    {
        var signature = new CryptoProvider().CreateSignatureFromObject(data!, ChecksumKey);
        var body = JsonSerializer.Serialize(new { code = "00", desc = "success", data, signature });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class PayosHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory = responseFactory;

        public List<string> RequestBodies { get; } = [];
        public List<string> RequestPaths { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            if (request.Content is not null)
                RequestBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));
            return _responseFactory(request);
        }
    }
}
