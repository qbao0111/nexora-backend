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
    public string RequestType { get; init; } = MomoRequestTypes.CaptureWallet;
    public string TestCustomerEmail { get; init; } = "sandbox@nexora.local";
    public int TimeoutSeconds { get; init; } = 35;
}

public static class MomoRequestTypes
{
    public const string CaptureWallet = "captureWallet";
    public const string PayWithCreditCard = "payWithCC";
}

public sealed class MomoPaymentProvider(HttpClient httpClient, IOptions<MomoOptions> options) : IPaymentProvider
{
    private const string MomoProviderName = "momo";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private readonly MomoOptions _options = options.Value;

    public string ProviderName => MomoProviderName;

    public string CreateProviderTransactionId(Guid orderId) => ToMomoOrderId(orderId);

    public async Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var orderId = request.ProviderTransactionId;
        var momoRequestId = ToMomoRequestId(request.OrderId);
        var orderInfo = $"Nexora {request.Currency} {request.AmountMinor}";
        var extraData = string.Empty;
        var requestType = NormalizeRequestType(_options.RequestType);
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
            $"requestType={requestType}");
        var body = new MomoCreateRequest(
            _options.PartnerCode,
            momoRequestId,
            request.AmountMinor,
            orderId,
            orderInfo,
            _options.RedirectUrl,
            _options.IpnUrl,
            requestType,
            extraData,
            "vi",
            true,
            CreateUserInfo(requestType),
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

        VerifyCreateResponseSignatureWhenPresent(payload);
        var payUrl = ValidatePayUrl(payload.PayUrl!);

        return new PaymentCheckout(ProviderName, orderId, payUrl);
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
        var momoRequestId = ToMomoQueryRequestId();
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
        var payload = await ReadAsync<MomoQueryResponse>(response, cancellationToken);
        if (payload.ResultCode != 0 || payload.Amount != request.AmountMinor) return null;
        if (!string.Equals(payload.PartnerCode, _options.PartnerCode, StringComparison.Ordinal) ||
            !string.Equals(payload.OrderId, request.ProviderTransactionId, StringComparison.Ordinal) ||
            !string.Equals(payload.RequestId, momoRequestId, StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán MoMo không khớp order.", BusinessErrorKind.Validation);

        var providerTransactionId = request.ProviderTransactionId;
        var orderId = request.OrderId;
        var paid = payload.ResultCode == 0;
        var providerEventId = $"{ProviderName}:{providerTransactionId}:{payload.TransId?.ToString(CultureInfo.InvariantCulture) ?? "0"}:{payload.ResultCode.ToString(CultureInfo.InvariantCulture)}";
        return new VerifiedPaymentEvent(
            providerEventId,
            orderId,
            providerTransactionId,
            payload.Amount,
            "VND",
            paid,
            DateTimeOffset.FromUnixTimeMilliseconds(payload.ResponseTime));
    }

    public static string Sign(string rawData, string secretKey)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData))).ToLowerInvariant();
    }

    public static string ToMomoOrderId(Guid orderId) => $"nexora_{orderId:N}";
    public static string ToMomoRequestId(Guid orderId) => $"req_{orderId:N}";
    public static string ToMomoQueryRequestId() => $"qry_{Guid.NewGuid():N}";

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new BusinessException("PAYMENT_PROVIDER_AUTH_FAILED", "MoMo sandbox authentication failed.", BusinessErrorKind.ExternalFailure);
        if (!response.IsSuccessStatusCode)
            throw await ProviderRejectedAsync(response, cancellationToken);
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

    private static async Task<BusinessException> ProviderRejectedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<MomoProviderErrorResponse>(JsonOptions, cancellationToken);
            if (payload is not null && payload.ResultCode == 11)
                return new BusinessException(
                    "PAYMENT_PROVIDER_ACCESS_DENIED",
                    "MoMo sandbox từ chối quyền truy cập phương thức thanh toán này.",
                    BusinessErrorKind.ExternalFailure);
        }
        catch (JsonException)
        {
            // Fall through to the generic provider availability error.
        }

        return new BusinessException("PAYMENT_PROVIDER_UNAVAILABLE", "MoMo sandbox hiện không khả dụng.", BusinessErrorKind.ExternalFailure);
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

    private static string NormalizeRequestType(string? requestType)
    {
        if (string.Equals(requestType, MomoRequestTypes.PayWithCreditCard, StringComparison.OrdinalIgnoreCase))
            return MomoRequestTypes.PayWithCreditCard;
        return MomoRequestTypes.CaptureWallet;
    }

    private MomoUserInfo? CreateUserInfo(string requestType) =>
        string.Equals(requestType, MomoRequestTypes.PayWithCreditCard, StringComparison.Ordinal)
            ? new MomoUserInfo(_options.TestCustomerEmail)
            : null;

    private void VerifyCreateResponseSignatureWhenPresent(MomoCreateResponse payload)
    {
        if (string.IsNullOrWhiteSpace(payload.Signature)) return;
        if (string.IsNullOrWhiteSpace(payload.RequestId) || string.IsNullOrWhiteSpace(payload.OrderId) || payload.ResponseTime is null)
            throw InvalidCreateResponse();
        var rawSignature = string.Join('&',
            $"accessKey={_options.AccessKey}",
            $"amount={payload.Amount.ToString(CultureInfo.InvariantCulture)}",
            $"orderId={payload.OrderId}",
            $"partnerCode={payload.PartnerCode}",
            $"payUrl={payload.PayUrl ?? string.Empty}",
            $"requestId={payload.RequestId}",
            $"responseTime={payload.ResponseTime.Value.ToString(CultureInfo.InvariantCulture)}",
            $"resultCode={payload.ResultCode.ToString(CultureInfo.InvariantCulture)}");
        var expected = Encoding.UTF8.GetBytes(Sign(rawSignature, _options.SecretKey));
        var supplied = Encoding.UTF8.GetBytes(payload.Signature.Trim().ToLowerInvariant());
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
            throw InvalidCreateResponse();
    }

    private string ValidatePayUrl(string payUrl)
    {
        if (!Uri.TryCreate(payUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_CHECKOUT_FAILED", "Không thể tạo phiên thanh toán.", BusinessErrorKind.ExternalFailure);
        var host = uri.Host.ToLowerInvariant();
        var allowedHosts = string.Equals(_options.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
            ? new[] { "test-payment.momo.vn" }
            : new[] { "test-payment.momo.vn", "payment.momo.vn" };
        if (!allowedHosts.Any(host.Equals))
            throw new BusinessException("PAYMENT_CHECKOUT_FAILED", "Không thể tạo phiên thanh toán.", BusinessErrorKind.ExternalFailure);
        return payUrl;
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
            $"{MomoProviderName}:{payload.OrderId}:{payload.TransId.ToString(CultureInfo.InvariantCulture)}:{payload.ResultCode.ToString(CultureInfo.InvariantCulture)}",
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

    private static BusinessException InvalidCreateResponse() =>
        new("PAYMENT_PROVIDER_INVALID_RESPONSE", "MoMo sandbox trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);

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
        MomoUserInfo? UserInfo,
        string Signature);

    private sealed record MomoUserInfo(string Email);

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
        string? QrCodeUrl,
        long? ResponseTime,
        string? Signature);

    private sealed record MomoQueryResponse(
        string PartnerCode,
        string RequestId,
        string OrderId,
        long Amount,
        string Message,
        long? TransId,
        string PayType,
        int ResultCode,
        long ResponseTime,
        string? ExtraData);

    private sealed record MomoProviderErrorResponse(int ResultCode, string Message);

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
