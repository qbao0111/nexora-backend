using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexora.Business.Common;
using Nexora.Integrations.Payments;

namespace Nexora.UnitTests;

public sealed class MomoPaymentProviderTests
{
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
        var provider = new MomoPaymentProvider(new HttpClient(), Options.Create(new MomoOptions
        {
            PartnerCode = "MOMO",
            AccessKey = "access",
            SecretKey = "secret",
            RedirectUrl = "https://frontend.test/payment-return",
            IpnUrl = "https://api.test/api/v1/webhooks/payments/momo"
        }));
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
}
