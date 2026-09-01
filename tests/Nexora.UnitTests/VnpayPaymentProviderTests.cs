using System.Net;
using System.Net.Http;
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

public sealed class VnpayPaymentProviderTests
{
    private static readonly Guid OrderId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset CreatedAt = new(2026, 9, 2, 1, 2, 3, TimeSpan.Zero);
    private static readonly VnpayOptions TestOptions = new()
    {
        Environment = "Sandbox",
        TmnCode = "DEMOV210",
        HashSecret = "secret",
        ReturnUrl = "http://localhost:3000/payment/return"
    };
    private const string ExpectedPayHashData = "vnp_Amount=4900000&vnp_Command=pay&vnp_CreateDate=20260902080203&vnp_CurrCode=VND&vnp_ExpireDate=20260902081703&vnp_IpAddr=203.0.113.10&vnp_Locale=vn&vnp_OrderInfo=Nexora order nx11111111222233334444555555555555&vnp_OrderType=other&vnp_ReturnUrl=http://localhost:3000/payment/return&vnp_TmnCode=DEMOV210&vnp_TxnRef=nx11111111222233334444555555555555&vnp_Version=2.1.0";
    private const string ExpectedPayHash = "f55dd2a8b0ba50b218119f73c32d730077d643088557546eb815f8c584d1632a192dcdde4f5f5c8ea3dcbb901ed09f2e2b0e124d94b0c63ef9877210d230194b";

    [Fact]
    public void HmacSha512UsesDeterministicLowercaseHexVector()
    {
        Assert.Equal(
            "db1595ae88a62fd151ec1cba81b98c39df82daae7b4cb9820f446d5bf02f1dcfca6683d88cab3e273f5963ab8ec469a746b5b19086371239f67d1e5f99a79440",
            VnpayPaymentProvider.SignSha512("hello", "secret"));
    }

    [Fact]
    public async Task CreateCheckoutUsesRawPayHashAndEncodedTransportQuery()
    {
        var provider = NewProvider();
        var txnRef = VnpayPaymentProvider.ToVnpayTxnRef(OrderId);
        var checkout = await provider.CreateCheckoutAsync(
            new PaymentOrderRequest(OrderId, 49_000, "VND", txnRef, CreatedAt, "203.0.113.10"), CancellationToken.None);

        Assert.Equal("vnpay", checkout.Provider);
        Assert.Equal(txnRef, checkout.ProviderTransactionId);
        var uri = new Uri(checkout.CheckoutUrl);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("sandbox.vnpayment.vn", uri.Host);
        Assert.Equal("/paymentv2/vpcpay.html", uri.AbsolutePath);
        var query = ParseQuery(uri.Query);
        Assert.Equal("4900000", query["vnp_Amount"]);
        Assert.Equal("20260902080203", query["vnp_CreateDate"]);
        Assert.Equal("20260902081703", query["vnp_ExpireDate"]);
        Assert.Equal("203.0.113.10", query["vnp_IpAddr"]);
        Assert.Equal(txnRef, query["vnp_TxnRef"]);
        Assert.Equal("2.1.0", query["vnp_Version"]);
        Assert.Equal(ExpectedPayHashData, VnpayPaymentProvider.BuildHashData(query
            .Where(item => item.Key != "vnp_SecureHash")
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)));
        Assert.Equal(ExpectedPayHash, query["vnp_SecureHash"]);
        Assert.Equal(ExpectedPayHash, TestSignSha512(ExpectedPayHashData, TestOptions.HashSecret));
        Assert.Contains("vnp_OrderInfo=Nexora+order+nx11111111222233334444555555555555", checkout.CheckoutUrl, StringComparison.Ordinal);
        Assert.Contains("vnp_ReturnUrl=http%3A%2F%2Flocalhost%3A3000%2Fpayment%2Freturn", checkout.CheckoutUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void TxnRefIsDeterministicAlphanumericAndReversible()
    {
        var txnRef = VnpayPaymentProvider.ToVnpayTxnRef(OrderId);

        Assert.Equal("nx11111111222233334444555555555555", txnRef);
        Assert.All(txnRef, ch => Assert.True(char.IsLetterOrDigit(ch)));
        Assert.True(VnpayPaymentProvider.TryParseVnpayTxnRef(txnRef, out var parsed));
        Assert.Equal(OrderId, parsed);
        Assert.False(VnpayPaymentProvider.TryParseVnpayTxnRef("nx-not-a-guid", out _));
    }

    [Fact]
    public void VnpayAmountMultipliesAndDividesActualVndByOneHundred()
    {
        Assert.Equal(18_900_000, VnpayPaymentProvider.ToVnpayAmount(189_000));
        Assert.Equal(189_000, VnpayPaymentProvider.FromVnpayAmount(18_900_000));
        var exception = Assert.Throws<BusinessException>(() => VnpayPaymentProvider.FromVnpayAmount(18_900_001));
        Assert.Equal("PAYMENT_AMOUNT_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task ArbitraryPaymentHostIsRejected()
    {
        var provider = NewProvider(new VnpayOptions
        {
            Environment = TestOptions.Environment,
            TmnCode = TestOptions.TmnCode,
            HashSecret = TestOptions.HashSecret,
            ReturnUrl = TestOptions.ReturnUrl,
            PaymentUrl = "https://example.test/paymentv2/vpcpay.html"
        });
        var exception = await Assert.ThrowsAsync<BusinessException>(() => provider.CreateCheckoutAsync(
            new PaymentOrderRequest(OrderId, 49_000, "VND", VnpayPaymentProvider.ToVnpayTxnRef(OrderId), CreatedAt, null), CancellationToken.None));
        Assert.Equal("PAYMENT_PROVIDER_INVALID_CONFIG", exception.Code);
    }

    [Fact]
    public async Task ValidIpnChecksumIsAcceptedAndPaidRequiresBothSuccessCodes()
    {
        var provider = NewProvider();
        var query = SignedIpn(responseCode: "00", transactionStatus: "00");

        var ev = await provider.VerifyWebhookAsync(new PaymentCallbackRequest("GET", query, EmptyHeaders(), ReadOnlyMemory<byte>.Empty), CancellationToken.None);

        Assert.True(ev.IsPaid);
        Assert.True(ev.IsFinal);
        Assert.Equal(OrderId, ev.OrderId);
        Assert.Equal(189_000, ev.AmountMinor);
        Assert.Equal($"vnpay:{ev.ProviderTransactionId}:987654:00:00", ev.ProviderEventId);

        var notPaid = await provider.VerifyWebhookAsync(new PaymentCallbackRequest("GET", SignedIpn("00", "02"), EmptyHeaders(), ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        Assert.False(notPaid.IsPaid);
        Assert.True(notPaid.IsFinal);

        var pending = await provider.VerifyWebhookAsync(new PaymentCallbackRequest("GET", SignedIpn("00", "01"), EmptyHeaders(), ReadOnlyMemory<byte>.Empty), CancellationToken.None);
        Assert.False(pending.IsPaid);
        Assert.False(pending.IsFinal);
    }

    [Fact]
    public async Task InvalidIpnChecksumAndWrongTmnCodeAreRejected()
    {
        var provider = NewProvider();
        var invalid = new Dictionary<string, string>(SignedIpn()) { ["vnp_SecureHash"] = "00" };

        var badSignature = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.VerifyWebhookAsync(new PaymentCallbackRequest("GET", invalid, EmptyHeaders(), ReadOnlyMemory<byte>.Empty), CancellationToken.None));
        Assert.Equal("INVALID_WEBHOOK_SIGNATURE", badSignature.Code);

        var wrongTmn = SignedIpn();
        wrongTmn["vnp_TmnCode"] = "OTHER";
        wrongTmn["vnp_SecureHash"] = SignQuery(wrongTmn);
        var wrongTmnException = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.VerifyWebhookAsync(new PaymentCallbackRequest("GET", wrongTmn, EmptyHeaders(), ReadOnlyMemory<byte>.Empty), CancellationToken.None));
        Assert.Equal("INVALID_WEBHOOK_SIGNATURE", wrongTmnException.Code);
    }

    [Fact]
    public async Task QueryDrRequestUsesOriginalTransactionDateAndPipeSignature()
    {
        string? body = null;
        var txnRef = VnpayPaymentProvider.ToVnpayTxnRef(OrderId);
        var response = SignedQueryResponse(txnRef, transactionStatus: "00");
        var provider = NewProvider(handler: new TestHandler(HttpStatusCode.OK, response, value => body = value));

        var ev = await provider.QueryPaymentAsync(new PaymentOrderRequest(OrderId, 189_000, "VND", txnRef, CreatedAt, null), CancellationToken.None);

        Assert.NotNull(ev);
        Assert.True(ev.IsPaid);
        using var document = JsonDocument.Parse(body!);
        var root = document.RootElement;
        Assert.Equal("querydr", root.GetProperty("vnp_Command").GetString());
        Assert.Equal("20260902080203", root.GetProperty("vnp_TransactionDate").GetString());
        var expectedRaw = string.Join('|',
            root.GetProperty("vnp_RequestId").GetString(),
            "2.1.0",
            "querydr",
            TestOptions.TmnCode,
            txnRef,
            "20260902080203",
            root.GetProperty("vnp_CreateDate").GetString(),
            "127.0.0.1",
            $"Nexora order {txnRef}");
        Assert.Equal(TestSignSha512(expectedRaw, TestOptions.HashSecret), root.GetProperty("vnp_SecureHash").GetString());
    }

    [Fact]
    public async Task QueryDrResponseSignatureAndTransactionStatusAreAuthoritative()
    {
        var txnRef = VnpayPaymentProvider.ToVnpayTxnRef(OrderId);
        var pending = NewProvider(handler: new TestHandler(HttpStatusCode.OK, SignedQueryResponse(txnRef, transactionStatus: "01")));
        Assert.Null(await pending.QueryPaymentAsync(new PaymentOrderRequest(OrderId, 189_000, "VND", txnRef, CreatedAt, null), CancellationToken.None));

        var failed = NewProvider(handler: new TestHandler(HttpStatusCode.OK, SignedQueryResponse(txnRef, transactionStatus: "02")));
        var failedEvent = await failed.QueryPaymentAsync(new PaymentOrderRequest(OrderId, 189_000, "VND", txnRef, CreatedAt, null), CancellationToken.None);
        Assert.NotNull(failedEvent);
        Assert.False(failedEvent.IsPaid);
        Assert.True(failedEvent.IsFinal);

        var notFound = NewProvider(handler: new TestHandler(HttpStatusCode.OK, SignedQueryResponse(txnRef, responseCode: "91", transactionStatus: "01")));
        Assert.Null(await notFound.QueryPaymentAsync(new PaymentOrderRequest(OrderId, 189_000, "VND", txnRef, CreatedAt, null), CancellationToken.None));

        var invalidJson = JsonSerializer.Deserialize<Dictionary<string, object?>>(SignedQueryResponse(txnRef))!;
        invalidJson["vnp_SecureHash"] = "bad";
        var invalid = NewProvider(handler: new TestHandler(HttpStatusCode.OK, JsonSerializer.Serialize(invalidJson)));
        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            invalid.QueryPaymentAsync(new PaymentOrderRequest(OrderId, 189_000, "VND", txnRef, CreatedAt, null), CancellationToken.None));
        Assert.Equal("PAYMENT_PROVIDER_INVALID_RESPONSE", exception.Code);
    }

    [Theory]
    [InlineData("02")]
    [InlineData("03")]
    [InlineData("08")]
    [InlineData("97")]
    [InlineData("99")]
    [InlineData("77")]
    public async Task QueryDrProtocolFailuresDoNotMasqueradeAsPending(string responseCode)
    {
        var txnRef = VnpayPaymentProvider.ToVnpayTxnRef(OrderId);
        var provider = NewProvider(handler: new TestHandler(HttpStatusCode.OK, SignedQueryResponse(txnRef, responseCode: responseCode)));

        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.QueryPaymentAsync(new PaymentOrderRequest(OrderId, 189_000, "VND", txnRef, CreatedAt, null), CancellationToken.None));

        Assert.Equal("PAYMENT_PROVIDER_QUERY_FAILED", exception.Code);
        Assert.Equal(BusinessErrorKind.ExternalFailure, exception.Kind);
    }

    [Theory]
    [InlineData("SHORT")]
    [InlineData("DEMOV21!")]
    [InlineData(" DEMOV21")]
    [InlineData("DEMOV210 ")]
    public void InvalidVnpayTmnCodeFailsConfigurationValidation(string tmnCode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:Ai"] = "false",
            ["Billing:Payment:Provider"] = "vnpay",
            ["Billing:Vnpay:Environment"] = "Sandbox",
            ["Billing:Vnpay:TmnCode"] = tmnCode,
            ["Billing:Vnpay:HashSecret"] = "secret",
            ["Billing:Vnpay:ReturnUrl"] = "http://localhost:3000/payment/return"
        }).Build();
        var services = new ServiceCollection().AddIntegrations(configuration);
        using var serviceProvider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => serviceProvider.GetRequiredService<IOptions<VnpayOptions>>().Value);
    }

    private static VnpayPaymentProvider NewProvider(VnpayOptions? options = null, HttpMessageHandler? handler = null) =>
        new(new HttpClient(handler ?? new TestHandler(HttpStatusCode.OK, "{}")), Options.Create(options ?? TestOptions), TimeProvider.System);

    private static Dictionary<string, string> SignedIpn(string responseCode = "00", string transactionStatus = "00")
    {
        var txnRef = VnpayPaymentProvider.ToVnpayTxnRef(OrderId);
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = "18900000",
            ["vnp_BankCode"] = "NCB",
            ["vnp_OrderInfo"] = $"Nexora order {txnRef}",
            ["vnp_PayDate"] = "20260902081500",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_TmnCode"] = TestOptions.TmnCode,
            ["vnp_TransactionNo"] = "987654",
            ["vnp_TransactionStatus"] = transactionStatus,
            ["vnp_TxnRef"] = txnRef
        };
        query["vnp_SecureHash"] = SignQuery(query);
        return query;
    }

    private static string SignQuery(IReadOnlyDictionary<string, string> query) =>
        TestSignSha512(BuildTestHashData(query), TestOptions.HashSecret);

    private static string BuildTestHashData(IReadOnlyDictionary<string, string> query) =>
        string.Join('&', query
            .Where(item => item.Key is not "vnp_SecureHash" and not "vnp_SecureHashType" && !string.IsNullOrEmpty(item.Value))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={item.Value}"));

    private static string TestSignSha512(string rawData, string secret)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData))).ToLowerInvariant();
    }

    private static string SignedQueryResponse(string txnRef, string responseCode = "00", string transactionStatus = "00")
    {
        var values = new Dictionary<string, object?>
        {
            ["vnp_ResponseId"] = "res123",
            ["vnp_Command"] = "querydr",
            ["vnp_ResponseCode"] = responseCode,
            ["vnp_Message"] = "Success",
            ["vnp_TmnCode"] = TestOptions.TmnCode,
            ["vnp_TxnRef"] = txnRef,
            ["vnp_Amount"] = 18_900_000,
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
        values["vnp_SecureHash"] = TestSignSha512(raw, TestOptions.HashSecret);
        return JsonSerializer.Serialize(values);
    }

    private static Dictionary<string, string> EmptyHeaders() => new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            result[WebUtility.UrlDecode(parts[0])] = WebUtility.UrlDecode(parts[1]);
        }
        return result;
    }

    private sealed class TestHandler(HttpStatusCode statusCode, string content, Action<string>? captureBody = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (captureBody is not null && request.Content is not null)
                captureBody(await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
        }
    }
}
