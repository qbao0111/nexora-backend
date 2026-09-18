using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Common;

namespace Nexora.Integrations.Payments;

public sealed class FakePaymentOptions
{
    public const string SectionName = "Billing:FakePayment";
    public string WebhookSecret { get; init; } = string.Empty;
    public int TimestampToleranceMinutes { get; init; } = 5;
}

public sealed class FakePaymentProvider(IOptions<FakePaymentOptions> options, TimeProvider timeProvider) : IPaymentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FakePaymentOptions _options = options.Value;
    public string ProviderName => "fake";

    public string CreateProviderTransactionId(Guid orderId) => $"fake_{orderId:N}";

    public CheckoutAction? RestoreCheckoutAction(string checkoutUrl) =>
        checkoutUrl.StartsWith("/fake-payments/", StringComparison.Ordinal)
            ? new CheckoutAction("GET", checkoutUrl, Array.Empty<CheckoutFormField>())
            : null;

    public Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PaymentCheckout(
            ProviderName,
            request.ProviderTransactionId,
            new CheckoutAction("GET", $"/fake-payments/{request.ProviderTransactionId}", Array.Empty<CheckoutFormField>())));
    }

    public Task<VerifiedPaymentEvent> VerifyWebhookAsync(PaymentCallbackRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var signature = request.Headers.TryGetValue("X-Payment-Signature", out var signatureValue) ? signatureValue : string.Empty;
        var timestamp = request.Headers.TryGetValue("X-Payment-Timestamp", out var timestampValue) ? timestampValue : string.Empty;
        var body = request.Body;
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret)) throw InvalidWebhook();
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var unixSeconds)) throw InvalidWebhook();
        var sentAt = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        if ((timeProvider.GetUtcNow() - sentAt).Duration() > TimeSpan.FromMinutes(_options.TimestampToleranceMinutes)) throw InvalidWebhook();

        byte[] supplied;
        try { supplied = Convert.FromHexString(signature); }
        catch (FormatException) { throw InvalidWebhook(); }
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.WebhookSecret));
        var signedBody = Encoding.UTF8.GetBytes($"{timestamp}.{Encoding.UTF8.GetString(body.Span)}");
        var expected = hmac.ComputeHash(signedBody);
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected)) throw InvalidWebhook();

        FakeWebhookPayload? payload;
        try { payload = JsonSerializer.Deserialize<FakeWebhookPayload>(body.Span, JsonOptions); }
        catch (JsonException) { throw InvalidPayload(); }
        if (payload is null || payload.OrderId == Guid.Empty || string.IsNullOrWhiteSpace(payload.EventId) ||
            string.IsNullOrWhiteSpace(payload.TransactionId) || string.IsNullOrWhiteSpace(payload.Status) || payload.OccurredAt == default)
            throw InvalidPayload();

        var status = payload.Status.Trim();

        return Task.FromResult(new VerifiedPaymentEvent(
            payload.EventId.Trim(),
            payload.OrderId,
            payload.TransactionId.Trim(),
            payload.AmountMinor,
            payload.Currency.Trim().ToUpperInvariant(),
            string.Equals(status, "paid", StringComparison.OrdinalIgnoreCase),
            !string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase),
            payload.OccurredAt));
    }

    public Task<VerifiedPaymentEvent?> QueryPaymentAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<VerifiedPaymentEvent?>(null);
    }

    private static BusinessException InvalidWebhook() =>
        new("INVALID_WEBHOOK_SIGNATURE", "Webhook thanh toán không hợp lệ.", BusinessErrorKind.Unauthorized);

    private static BusinessException InvalidPayload() =>
        new("INVALID_WEBHOOK_PAYLOAD", "Dữ liệu webhook thanh toán không hợp lệ.", BusinessErrorKind.Validation);

    private sealed record FakeWebhookPayload(string EventId, Guid OrderId, string TransactionId, long AmountMinor, string Currency, string Status, DateTimeOffset OccurredAt);
}
