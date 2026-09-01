using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using Nexora.Integrations.Payments;

namespace Nexora.UnitTests;

public sealed class MomoPaymentProviderTests
{
    private static readonly MomoOptions TestOptions = new()
    {
        PartnerCode = "MOMO",
        AccessKey = "access",
        SecretKey = "secret",
        RedirectUrl = "https://frontend.test/payment-return",
        IpnUrl = "https://api.test/api/v1/webhooks/payments/momo"
    };

    [Fact]
    public void CreateSignatureUsesMoMoCanonicalOrder()
    {
        const string secret = "secret";
        const string raw = "accessKey=access&amount=49000&extraData=&ipnUrl=https://api.test/momo&orderId=nexora_order&orderInfo=Nexora VND 49000&partnerCode=partner&redirectUrl=https://app.test/pricing&requestId=req_order&requestType=captureWallet";

        Assert.Equal("f33060f3121b5a4665865a85fe91493f523fbe24b675227fc613b510559d702f", MomoPaymentProvider.Sign(raw, secret));
    }

    [Fact]
    public async Task InvalidIpnSignatureIsRejected()
    {
        var provider = new MomoPaymentProvider(new HttpClient(), Options.Create(TestOptions));
        var orderId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            partnerCode = "MOMO",
            orderId = MomoPaymentProvider.ToMomoOrderId(orderId),
            requestId = MomoPaymentProvider.ToMomoRequestId(orderId),
            amount = "49000",
            orderInfo = "Nexora VND 49000",
            orderType = "momo_wallet",
            transId = "123456",
            resultCode = 0,
            message = "Successful.",
            payType = "qr",
            responseTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            extraData = "",
            signature = "bad-signature"
        });

        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.VerifyWebhookAsync("", "", body, CancellationToken.None));
        Assert.Equal("INVALID_WEBHOOK_SIGNATURE", exception.Code);
    }

    [Fact]
    public void QueryRequestIdDiffersFromCreateRequestId()
    {
        var orderId = Guid.NewGuid();
        var createRequestId = MomoPaymentProvider.ToMomoRequestId(orderId);
        var queryRequestId1 = MomoPaymentProvider.ToMomoQueryRequestId();
        var queryRequestId2 = MomoPaymentProvider.ToMomoQueryRequestId();

        Assert.StartsWith("req_", createRequestId, StringComparison.Ordinal);
        Assert.StartsWith("qry_", queryRequestId1, StringComparison.Ordinal);
        Assert.StartsWith("qry_", queryRequestId2, StringComparison.Ordinal);
        Assert.NotEqual(createRequestId, queryRequestId1);
        Assert.NotEqual(queryRequestId1, queryRequestId2);
    }

    [Fact]
    public async Task CreateCheckoutWithValidSignatureAndAllowedHostSucceeds()
    {
        var orderId = Guid.NewGuid();
        var providerTxId = MomoPaymentProvider.ToMomoOrderId(orderId);
        var requestId = MomoPaymentProvider.ToMomoRequestId(orderId);
        const long amount = 49000;
        const string payUrl = "https://test-payment.momo.vn/v2/gateway/pay?s=123";
        const long responseTime = 1700000000000;

        var rawSig = $"accessKey=access&amount={amount}&message=Success&orderId={providerTxId}&partnerCode=MOMO&payUrl={payUrl}&requestId={requestId}&responseTime={responseTime}&resultCode=0";
        var signature = MomoPaymentProvider.Sign(rawSig, "secret");

        var responseJson = JsonSerializer.Serialize(new
        {
            partnerCode = "MOMO",
            requestId,
            orderId = providerTxId,
            amount,
            resultCode = 0,
            message = "Success",
            payUrl,
            responseTime,
            signature
        });

        var handler = new TestHttpMessageHandler(HttpStatusCode.OK, responseJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test-payment.momo.vn") };
        var provider = new MomoPaymentProvider(httpClient, Options.Create(TestOptions));

        var checkout = await provider.CreateCheckoutAsync(
            new PaymentOrderRequest(orderId, amount, "VND", providerTxId), CancellationToken.None);

        Assert.Equal("momo", checkout.Provider);
        Assert.Equal(providerTxId, checkout.ProviderTransactionId);
        Assert.Equal(payUrl, checkout.CheckoutUrl);
    }

    [Fact]
    public async Task CreateCheckoutWithInvalidResponseSignatureIsRejected()
    {
        var orderId = Guid.NewGuid();
        var providerTxId = MomoPaymentProvider.ToMomoOrderId(orderId);
        var requestId = MomoPaymentProvider.ToMomoRequestId(orderId);
        const long amount = 49000;
        const string payUrl = "https://test-payment.momo.vn/v2/gateway/pay?s=123";

        var responseJson = JsonSerializer.Serialize(new
        {
            partnerCode = "MOMO",
            requestId,
            orderId = providerTxId,
            amount,
            resultCode = 0,
            message = "Success",
            payUrl,
            responseTime = 1700000000000L,
            signature = "invalid-signature"
        });

        var handler = new TestHttpMessageHandler(HttpStatusCode.OK, responseJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test-payment.momo.vn") };
        var provider = new MomoPaymentProvider(httpClient, Options.Create(TestOptions));

        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.CreateCheckoutAsync(new PaymentOrderRequest(orderId, amount, "VND", providerTxId), CancellationToken.None));
        Assert.Equal("PAYMENT_PROVIDER_INVALID_RESPONSE", exception.Code);
    }

    [Fact]
    public async Task CreateCheckoutWithDisallowedHostIsRejected()
    {
        var orderId = Guid.NewGuid();
        var providerTxId = MomoPaymentProvider.ToMomoOrderId(orderId);
        var requestId = MomoPaymentProvider.ToMomoRequestId(orderId);
        const long amount = 49000;
        const string payUrl = "https://evil-phishing.com/pay";
        const long responseTime = 1700000000000;

        var rawSig = $"accessKey=access&amount={amount}&message=Success&orderId={providerTxId}&partnerCode=MOMO&payUrl={payUrl}&requestId={requestId}&responseTime={responseTime}&resultCode=0";
        var signature = MomoPaymentProvider.Sign(rawSig, "secret");

        var responseJson = JsonSerializer.Serialize(new
        {
            partnerCode = "MOMO",
            requestId,
            orderId = providerTxId,
            amount,
            resultCode = 0,
            message = "Success",
            payUrl,
            responseTime,
            signature
        });

        var handler = new TestHttpMessageHandler(HttpStatusCode.OK, responseJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test-payment.momo.vn") };
        var provider = new MomoPaymentProvider(httpClient, Options.Create(TestOptions));

        var exception = await Assert.ThrowsAsync<BusinessException>(() =>
            provider.CreateCheckoutAsync(new PaymentOrderRequest(orderId, amount, "VND", providerTxId), CancellationToken.None));
        Assert.Equal("PAYMENT_CHECKOUT_FAILED", exception.Code);
    }

    [Fact]
    public async Task QueryPaymentDoesNotRequireIpnSignatureAndReturnsVerifiedEvent()
    {
        var orderId = Guid.NewGuid();
        var providerTxId = MomoPaymentProvider.ToMomoOrderId(orderId);
        const long amount = 49000;
        const long transId = 987654321;
        const long responseTime = 1700000000000;

        var responseJson = JsonSerializer.Serialize(new
        {
            partnerCode = "MOMO",
            requestId = "qry_placeholder",
            orderId = providerTxId,
            amount,
            message = "Successful.",
            transId,
            payType = "qr",
            resultCode = 0,
            responseTime
        });

        var handler = new InterceptingMomoQueryHandler(responseJson);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://test-payment.momo.vn") };
        var provider = new MomoPaymentProvider(httpClient, Options.Create(TestOptions));

        var ev = await provider.QueryPaymentAsync(
            new PaymentOrderRequest(orderId, amount, "VND", providerTxId), CancellationToken.None);

        Assert.NotNull(ev);
        Assert.True(ev.IsPaid);
        Assert.Equal(orderId, ev.OrderId);
        Assert.Equal(providerTxId, ev.ProviderTransactionId);
        Assert.Equal(amount, ev.AmountMinor);
        Assert.Equal($"momo:{providerTxId}:{transId}:0", ev.ProviderEventId);
    }

    private sealed class TestHttpMessageHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class InterceptingMomoQueryHandler(string templateJson) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var reqDoc = JsonDocument.Parse(body);
            var requestId = reqDoc.RootElement.GetProperty("requestId").GetString();

            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(templateJson)!;
            dict["requestId"] = requestId!;
            var patchedJson = JsonSerializer.Serialize(dict);

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(patchedJson, Encoding.UTF8, "application/json")
            };
            return response;
        }
    }
}
