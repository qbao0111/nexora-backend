using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Business.Billing;
using Nexora.Business.Common;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1/webhooks/payments")]
public sealed class PaymentWebhooksController(IBillingService billingService) : ControllerBase
{
    private const int MaximumPayloadBytes = 64 * 1024;

    [AllowAnonymous, HttpPost("fake")]
    public async Task<IActionResult> ReceiveFake(CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(cancellationToken);
        await billingService.ProcessPaymentWebhookAsync(
            "fake",
            new PaymentCallbackRequest(
                Request.Method,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Payment-Signature"] = Request.Headers["X-Payment-Signature"].ToString(),
                    ["X-Payment-Timestamp"] = Request.Headers["X-Payment-Timestamp"].ToString()
                },
                body),
            cancellationToken);
        return NoContent();
    }

    [AllowAnonymous, HttpPost("sepay")]
    public async Task<IActionResult> ReceiveSepay(CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(cancellationToken);
        await billingService.ProcessPaymentWebhookAsync(
            "sepay",
            new PaymentCallbackRequest(
                Request.Method,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Secret-Key"] = Request.Headers["X-Secret-Key"].ToString()
                },
                body),
            cancellationToken);
        return Ok(new { success = true });
    }

    private async Task<ReadOnlyMemory<byte>> ReadBodyAsync(CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaximumPayloadBytes)
            throw new BusinessException("WEBHOOK_PAYLOAD_TOO_LARGE", "Webhook thanh toán quá lớn.", BusinessErrorKind.Validation);
        await using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaximumPayloadBytes)
            throw new BusinessException("WEBHOOK_PAYLOAD_TOO_LARGE", "Webhook thanh toán quá lớn.", BusinessErrorKind.Validation);
        return buffer.ToArray();
    }
}
