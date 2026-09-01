using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Business.Billing;
using Nexora.Business.Common;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1/webhooks/payments/{provider}")]
public sealed class PaymentWebhooksController(IBillingService billingService) : ControllerBase
{
    private const int MaximumPayloadBytes = 64 * 1024;

    [AllowAnonymous, HttpPost]
    public async Task<IActionResult> Receive(string provider, CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaximumPayloadBytes)
            throw new BusinessException("WEBHOOK_PAYLOAD_TOO_LARGE", "Webhook thanh toán quá lớn.", BusinessErrorKind.Validation);
        await using var buffer = new MemoryStream();
        await Request.Body.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length > MaximumPayloadBytes)
            throw new BusinessException("WEBHOOK_PAYLOAD_TOO_LARGE", "Webhook thanh toán quá lớn.", BusinessErrorKind.Validation);
        await billingService.ProcessPaymentWebhookAsync(
            provider,
            Request.Headers["X-Payment-Signature"].ToString(),
            Request.Headers["X-Payment-Timestamp"].ToString(),
            buffer.ToArray(),
            cancellationToken);
        return NoContent();
    }
}
