using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Business.Billing;
using Nexora.Business.Common;

namespace Nexora.Api.Controllers;

[ApiController, Route("api/v1/webhooks/payments/{provider}")]
public sealed class PaymentWebhooksController(IBillingService billingService) : ControllerBase
{
    private const int MaximumPayloadBytes = 64 * 1024;
    private const int MaximumVnpayParameters = 64;
    private const int MaximumVnpayValueLength = 1024;

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
            new PaymentCallbackRequest(
                Request.Method,
                new Dictionary<string, string>(StringComparer.Ordinal),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Payment-Signature"] = Request.Headers["X-Payment-Signature"].ToString(),
                    ["X-Payment-Timestamp"] = Request.Headers["X-Payment-Timestamp"].ToString()
                },
                buffer.ToArray()),
            cancellationToken);
        return NoContent();
    }

    [AllowAnonymous, HttpGet]
    public async Task<IActionResult> ReceiveVnpay(string provider, CancellationToken cancellationToken)
    {
        if (!string.Equals(provider, "vnpay", StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_PROVIDER_NOT_SUPPORTED", "Cổng thanh toán không được hỗ trợ.", BusinessErrorKind.NotFound);
        if (!TryReadVnpayQuery(out var query))
            return Ok(new VnpayIpnResponse("99", "Unknown error"));

        try
        {
            var result = await billingService.ProcessPaymentWebhookAsync(
                provider,
                new PaymentCallbackRequest(Request.Method, query, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ReadOnlyMemory<byte>.Empty),
                cancellationToken);
            return Ok(result.WasDuplicate || result.WasAlreadyFinal
                ? new VnpayIpnResponse("02", "Order already confirmed")
                : new VnpayIpnResponse("00", "Confirm Success"));
        }
        catch (BusinessException exception)
        {
            return Ok(MapVnpayError(exception));
        }
    }

    private bool TryReadVnpayQuery(out IReadOnlyDictionary<string, string> query)
    {
        query = new Dictionary<string, string>(StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in Request.Query)
        {
            if (!item.Key.StartsWith("vnp_", StringComparison.Ordinal) ||
                item.Key.Length > 64 ||
                item.Value.Count != 1 ||
                item.Value[0] is null ||
                item.Value[0]!.Length > MaximumVnpayValueLength)
                return false;
            values[item.Key] = item.Value[0]!;
            if (values.Count > MaximumVnpayParameters) return false;
        }
        query = values;
        return true;
    }

    private static VnpayIpnResponse MapVnpayError(BusinessException exception) =>
        exception.Code switch
        {
            "INVALID_WEBHOOK_SIGNATURE" => new VnpayIpnResponse("97", "Invalid signature"),
            "ORDER_NOT_FOUND" => new VnpayIpnResponse("01", "Order not found"),
            "PAYMENT_AMOUNT_MISMATCH" => new VnpayIpnResponse("04", "Invalid amount"),
            _ => new VnpayIpnResponse("99", "Unknown error")
        };

    private sealed record VnpayIpnResponse(
        [property: JsonPropertyName("RspCode")] string RspCode,
        [property: JsonPropertyName("Message")] string Message);
}
