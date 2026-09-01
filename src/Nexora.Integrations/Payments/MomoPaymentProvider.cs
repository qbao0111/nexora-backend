using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Common;

namespace Nexora.Integrations.Payments;

public sealed class MomoOptions
{
    public const string SectionName = "Billing:MoMo";
    public string Environment { get; init; } = "Sandbox";
    public string PartnerCode { get; init; } = string.Empty;
    public string AccessKey { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string RedirectUrl { get; init; } = string.Empty;
    public string IpnUrl { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 35;
}

public sealed class MomoPaymentProvider(HttpClient httpClient, IOptions<MomoOptions> options) : IPaymentProvider
{
    private const string RequestType = "captureWallet";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly MomoOptions _options = options.Value;

    public string ProviderName => "momo";

    public string CreateProviderTransactionId(Guid orderId) => ToMomoOrderId(orderId);

    public async Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var orderId = request.ProviderTransactionId;
        var momoRequestId = ToMomoRequestId(request.OrderId);
        var orderInfo = $"Nexora {request.Currency} {request.AmountMinor}";
        var extraData = string.Empty;
        var rawSignature = string.Join('&',
            $"accessKey={_options.AccessKey}",
            $"amount={request.AmountMinor.ToString(CultureInfo.InvariantCulture)}",
            $"extraData={extraData}",
            $"ipnUrl={_options.IpnUrl}",
            $"orderId={orderId}",
            $"orderInfo={orderInfo}",
            $"partnerCode={_options.PartnerCode}",
            $"redirectUrl={_options.RedirectUrl}",
            $"requestId={momoRequestId}",
            $"requestType={RequestType}");
        var body = new MomoCreateRequest(
            _options.PartnerCode,
            momoRequestId,
            request.AmountMinor,
            orderId,
            orderInfo,
            _options.RedirectUrl,
            _options.IpnUrl,
            RequestType,
            extraData,
            "vi",
            true,
            Sign(rawSignature, _options.SecretKey));

        using var response = await httpClient.PostAsJsonAsync("/v2/gateway/api/create", body, JsonOptions, cancellationToken);
        var payload = await ReadAsync<MomoCreateResponse>(response, cancellationToken);
        if (payload.ResultCode != 0 || string.IsNullOrWhiteSpace(payload.PayUrl))
            throw new BusinessException("PAYMENT_CHECKOUT_FAILED", "Không thể tạo phiên thanh toán MoMo sandbox.", BusinessErrorKind.ExternalFailure);
        if (!string.Equals(payload.PartnerCode, _options.PartnerCode, StringComparison.Ordinal) ||
            !string.Equals(payload.RequestId, momoRequestId, StringComparison.Ordinal) ||
            !string.Equals(payload.OrderId, orderId, StringComparison.Ordinal) ||
            payload.Amount != request.AmountMinor)
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán MoMo không khớp order.", BusinessErrorKind.Validation);

        return new PaymentCheckout(ProviderName, orderId, payload.PayUrl);
    }

    public Task<VerifiedPaymentEvent> VerifyWebhookAsync(string signature, string timestamp, ReadOnlyMemory<byte> body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MomoPaymentPayload? payload;
        try { payload = JsonSerializer.Deserialize<MomoPaymentPayload>(body.Span, JsonOptions); }
        catch (JsonException) { throw InvalidPayload(); }
        if (payload is null) throw InvalidPayload();
        if (!VerifySignature(payload)) throw InvalidWebhook();
        return Task.FromResult(ToVerifiedEvent(payload));
    }

    public async Task<VerifiedPaymentEvent?> QueryPaymentAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var momoRequestId = ToMomoRequestId(request.OrderId);
        var rawSignature = string.Join('&',
            $"accessKey={_options.AccessKey}",
            $"orderId={request.ProviderTransactionId}",
            $"partnerCode={_options.PartnerCode}",
            $"requestId={momoRequestId}");
        var body = new MomoQueryRequest(
            _options.PartnerCode,
            momoRequestId,
            request.ProviderTransactionId,
            "vi",
            Sign(rawSignature, _options.SecretKey));

        using var response = await httpClient.PostAsJsonAsync("/v2/gateway/api/query", body, JsonOptions, cancellationToken);
        var payload = await ReadAsync<MomoPaymentPayload>(response, cancellationToken);
        return payload.ResultCode == 0 && VerifySignature(payload) ? ToVerifiedEvent(payload) : null;
    }

    public static string Sign(string rawData, string secretKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData))).ToLowerInvariant();
    }

    public static string ToMomoOrderId(Guid orderId) => $"nexora_{orderId:N}";
    public static string ToMomoRequestId(Guid orderId) => $"req_{orderId:N}";

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BusinessException("PAYMENT_PROVIDER_AUTH_FAILED", "MoMo sandbox authentication failed.", BusinessErrorKind.ExternalFailure);
        if (!response.IsSuccessStatusCode)
            throw new BusinessException("PAYMENT_PROVIDER_UNAVAILABLE", "MoMo sandbox hiện không khả dụng.", BusinessErrorKind.ExternalFailure);
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken)
                ?? throw new BusinessException("PAYMENT_PROVIDER_INVALID_RESPONSE", "MoMo sandbox trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
        }
        catch (JsonException)
        {
            throw new BusinessException("PAYMENT_PROVIDER_INVALID_RESPONSE", "MoMo sandbox trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);
        }
    }

    private static void ValidateRequest(PaymentOrderRequest request)
    {
        if (!string.Equals(request.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_CURRENCY_NOT_SUPPORTED", "MoMo sandbox chỉ hỗ trợ VND.", BusinessErrorKind.Validation);
        if (request.AmountMinor is < 1_000 or > 50_000_000)
            throw new BusinessException("PAYMENT_AMOUNT_NOT_SUPPORTED", "Số tiền MoMo sandbox phải từ 1.000đ đến 50.000.000đ.", BusinessErrorKind.Validation);
        if (!string.Equals(request.ProviderTransactionId, ToMomoOrderId(request.OrderId), StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán MoMo không khớp order.", BusinessErrorKind.Validation);
    }

    private bool VerifySignature(MomoPaymentPayload payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Signature) ||
            string.IsNullOrWhiteSpace(payload.OrderId) ||
            string.IsNullOrWhiteSpace(payload.RequestId) ||
            !string.Equals(payload.PartnerCode, _options.PartnerCode, StringComparison.Ordinal))
            return false;
        var rawSignature = string.Join('&',
            $"accessKey={_options.AccessKey}",
            $"amount={payload.Amount.ToString(CultureInfo.InvariantCulture)}",
            $"extraData={payload.ExtraData ?? string.Empty}",
            $"message={payload.Message}",
            $"orderId={payload.OrderId}",
            $"orderInfo={payload.OrderInfo}",
            $"orderType={payload.OrderType}",
            $"partnerCode={payload.PartnerCode}",
            $"payType={payload.PayType}",
            $"requestId={payload.RequestId}",
            $"responseTime={payload.ResponseTime}",
            $"resultCode={payload.ResultCode.ToString(CultureInfo.InvariantCulture)}",
            $"transId={payload.TransId.ToString(CultureInfo.InvariantCulture)}");
        var expected = Encoding.UTF8.GetBytes(Sign(rawSignature, _options.SecretKey));
        var supplied = Encoding.UTF8.GetBytes(payload.Signature.Trim().ToLowerInvariant());
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static VerifiedPaymentEvent ToVerifiedEvent(MomoPaymentPayload payload)
    {
        if (!TryParseMomoOrderId(payload.OrderId, out var orderId) ||
            !string.Equals(payload.RequestId, ToMomoRequestId(orderId), StringComparison.Ordinal))
            throw InvalidPayload();
        return new VerifiedPaymentEvent(
            $"momo:{payload.TransId.ToString(CultureInfo.InvariantCulture)}",
            orderId,
            payload.OrderId,
            payload.Amount,
            "VND",
            payload.ResultCode == 0,
            DateTimeOffset.FromUnixTimeMilliseconds(payload.ResponseTime));
    }

    private static bool TryParseMomoOrderId(string value, out Guid orderId)
    {
        orderId = Guid.Empty;
        return value.StartsWith("nexora_", StringComparison.Ordinal) &&
            Guid.TryParseExact(value["nexora_".Length..], "N", out orderId);
    }

    private static BusinessException InvalidWebhook() =>
        new("INVALID_WEBHOOK_SIGNATURE", "Webhook thanh toán không hợp lệ.", BusinessErrorKind.Unauthorized);

    private static BusinessException InvalidPayload() =>
        new("INVALID_WEBHOOK_PAYLOAD", "Dữ liệu webhook thanh toán không hợp lệ.", BusinessErrorKind.Validation);

    private sealed record MomoCreateRequest(
        string PartnerCode,
        string RequestId,
        long Amount,
        string OrderId,
        string OrderInfo,
        string RedirectUrl,
        string IpnUrl,
        string RequestType,
        string ExtraData,
        string Lang,
        bool AutoCapture,
        string Signature);

    private sealed record MomoQueryRequest(string PartnerCode, string RequestId, string OrderId, string Lang, string Signature);

    private sealed record MomoCreateResponse(
        string PartnerCode,
        string RequestId,
        string OrderId,
        long Amount,
        int ResultCode,
        string Message,
        string? PayUrl,
        string? Deeplink,
        string? QrCodeUrl);

    private sealed record MomoPaymentPayload(
        string PartnerCode,
        string OrderId,
        string RequestId,
        long Amount,
        string OrderInfo,
        string OrderType,
        long TransId,
        int ResultCode,
        string Message,
        string PayType,
        long ResponseTime,
        string? ExtraData,
        string Signature);
}
