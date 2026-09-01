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

public sealed class VnpayOptions
{
    public const string SectionName = "Billing:Vnpay";
    public string Environment { get; init; } = "Sandbox";
    public string TmnCode { get; init; } = string.Empty;
    public string HashSecret { get; init; } = string.Empty;
    public string ReturnUrl { get; init; } = string.Empty;
    public string PaymentUrl { get; init; } = "https://sandbox.vnpayment.vn/paymentv2/vpcpay.html";
    public string QueryUrl { get; init; } = "https://sandbox.vnpayment.vn/merchant_webapi/api/transaction";
    public string Locale { get; init; } = "vn";
    public string OrderType { get; init; } = "other";
    public int ExpireMinutes { get; init; } = 15;
    public int TimeoutSeconds { get; init; } = 35;
}

public sealed class VnpayPaymentProvider(HttpClient httpClient, IOptions<VnpayOptions> options, TimeProvider timeProvider) : IPaymentProvider
{
    public const string Version = "2.1.0";
    private const string Provider = "vnpay";
    private static readonly TimeSpan VietnamOffset = TimeSpan.FromHours(7);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly VnpayOptions _options = options.Value;

    public string ProviderName => Provider;

    public string CreateProviderTransactionId(Guid orderId) => ToVnpayTxnRef(orderId);

    public Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOrderRequest(request);
        ValidatePaymentEndpoint(_options.PaymentUrl);
        var createDate = FormatVnpayDate(request.CreatedAt);
        var expireDate = FormatVnpayDate(request.CreatedAt.AddMinutes(_options.ExpireMinutes));
        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = ToVnpayAmount(request.AmountMinor).ToString(CultureInfo.InvariantCulture),
            ["vnp_Command"] = "pay",
            ["vnp_CreateDate"] = createDate,
            ["vnp_CurrCode"] = "VND",
            ["vnp_ExpireDate"] = expireDate,
            ["vnp_IpAddr"] = NormalizeIpAddress(request.IpAddress),
            ["vnp_Locale"] = NormalizeLocale(_options.Locale),
            ["vnp_OrderInfo"] = OrderInfo(request.ProviderTransactionId),
            ["vnp_OrderType"] = NormalizeOrderType(_options.OrderType),
            ["vnp_ReturnUrl"] = _options.ReturnUrl,
            ["vnp_TmnCode"] = _options.TmnCode,
            ["vnp_TxnRef"] = request.ProviderTransactionId,
            ["vnp_Version"] = Version
        };
        var hashData = BuildHashData(parameters);
        parameters["vnp_SecureHash"] = SignSha512(hashData, _options.HashSecret);
        var checkoutUrl = $"{_options.PaymentUrl}?{BuildQueryString(parameters)}";
        return Task.FromResult(new PaymentCheckout(ProviderName, request.ProviderTransactionId, checkoutUrl));
    }

    public Task<VerifiedPaymentEvent> VerifyWebhookAsync(PaymentCallbackRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Method, HttpMethod.Get.Method, StringComparison.OrdinalIgnoreCase))
            throw InvalidPayload();
        var parameters = request.QueryParameters;
        VerifyQuerySignature(parameters);
        return Task.FromResult(ToVerifiedEvent(parameters, timeProvider.GetUtcNow()));
    }

    public async Task<VerifiedPaymentEvent?> QueryPaymentAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        ValidateOrderRequest(request);
        ValidateQueryEndpoint(_options.QueryUrl);
        var queryRequestId = Guid.NewGuid().ToString("N");
        var now = timeProvider.GetUtcNow();
        var createDate = FormatVnpayDate(now);
        var transactionDate = FormatVnpayDate(request.CreatedAt);
        var orderInfo = OrderInfo(request.ProviderTransactionId);
        var ipAddress = NormalizeIpAddress(request.IpAddress);
        var rawHash = string.Join('|',
            queryRequestId,
            Version,
            "querydr",
            _options.TmnCode,
            request.ProviderTransactionId,
            transactionDate,
            createDate,
            ipAddress,
            orderInfo);
        var body = new VnpayQueryRequest(
            queryRequestId,
            Version,
            "querydr",
            _options.TmnCode,
            request.ProviderTransactionId,
            orderInfo,
            transactionDate,
            createDate,
            ipAddress,
            SignSha512(rawHash, _options.HashSecret));

        using var response = await httpClient.PostAsJsonAsync(_options.QueryUrl, body, JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new BusinessException("PAYMENT_PROVIDER_UNAVAILABLE", "VNPAY sandbox hiện không khả dụng.", BusinessErrorKind.ExternalFailure);
        var payload = await response.Content.ReadFromJsonAsync<VnpayQueryResponse>(JsonOptions, cancellationToken)
            ?? throw InvalidResponse();
        VerifyQueryResponseSignature(payload);
        if (!string.Equals(payload.Command, "querydr", StringComparison.Ordinal) ||
            !string.Equals(payload.TmnCode, _options.TmnCode, StringComparison.Ordinal) ||
            !string.Equals(payload.TxnRef, request.ProviderTransactionId, StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán không khớp order.", BusinessErrorKind.Validation);
        if (payload.ResponseCode == "91") return null;
        if (!string.Equals(payload.ResponseCode, "00", StringComparison.Ordinal)) throw QueryFailed();
        if (string.IsNullOrWhiteSpace(payload.TransactionStatus)) throw InvalidResponse();
        var amountMinor = FromVnpayAmount(payload.Amount);
        if (amountMinor != request.AmountMinor)
            throw new BusinessException("PAYMENT_AMOUNT_MISMATCH", "Số tiền thanh toán không khớp order.", BusinessErrorKind.Validation);
        if (payload.TransactionStatus == "01") return null;
        return ToVerifiedEvent(
            payload.TxnRef,
            amountMinor,
            payload.ResponseCode,
            payload.TransactionStatus,
            payload.TransactionNo ?? "0",
            payload.PayDate,
            timeProvider.GetUtcNow());
    }

    public static string ToVnpayTxnRef(Guid orderId) => $"nx{orderId:N}";

    public static bool TryParseVnpayTxnRef(string? value, out Guid orderId)
    {
        orderId = Guid.Empty;
        return value is { Length: 34 } &&
            value.StartsWith("nx", StringComparison.Ordinal) &&
            value[2..].All(Uri.IsHexDigit) &&
            Guid.TryParseExact(value[2..], "N", out orderId);
    }

    public static long ToVnpayAmount(long amountMinor) => checked(amountMinor * 100);

    public static long FromVnpayAmount(long vnpAmount)
    {
        if (vnpAmount <= 0 || vnpAmount % 100 != 0)
            throw new BusinessException("PAYMENT_AMOUNT_MISMATCH", "Số tiền thanh toán không khớp order.", BusinessErrorKind.Validation);
        return vnpAmount / 100;
    }

    public static string SignSha512(string rawData, string secret)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawData))).ToLowerInvariant();
    }

    public static string BuildHashData(IReadOnlyDictionary<string, string> parameters) =>
        string.Join('&', parameters
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{item.Key}={item.Value}"));

    public static string BuildQueryString(IReadOnlyDictionary<string, string> parameters) =>
        string.Join('&', parameters
            .Where(item => !string.IsNullOrEmpty(item.Value))
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => $"{Encode(item.Key)}={Encode(item.Value)}"));

    public static string FormatVnpayDate(DateTimeOffset value) =>
        value.ToUniversalTime().ToOffset(VietnamOffset).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);

    public static bool IsValidTmnCode(string? value) =>
        value is { Length: 8 } && value.All(char.IsLetterOrDigit);

    private void VerifyQuerySignature(IReadOnlyDictionary<string, string> parameters)
    {
        if (!parameters.TryGetValue("vnp_SecureHash", out var secureHash) || string.IsNullOrWhiteSpace(secureHash))
            throw InvalidWebhook();
        var signed = parameters
            .Where(item => item.Key.StartsWith("vnp_", StringComparison.Ordinal) &&
                item.Key is not "vnp_SecureHash" and not "vnp_SecureHashType" &&
                !string.IsNullOrEmpty(item.Value))
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        var expected = SignSha512(BuildHashData(signed), _options.HashSecret);
        if (!FixedEquals(secureHash, expected)) throw InvalidWebhook();
    }

    private VerifiedPaymentEvent ToVerifiedEvent(IReadOnlyDictionary<string, string> parameters, DateTimeOffset receivedAt)
    {
        var tmnCode = Required(parameters, "vnp_TmnCode");
        if (!string.Equals(tmnCode, _options.TmnCode, StringComparison.Ordinal))
            throw InvalidWebhook();
        var txnRef = Required(parameters, "vnp_TxnRef");
        var responseCode = Required(parameters, "vnp_ResponseCode");
        var transactionStatus = Required(parameters, "vnp_TransactionStatus");
        var transactionNo = Required(parameters, "vnp_TransactionNo");
        var payDate = parameters.TryGetValue("vnp_PayDate", out var payDateValue) ? payDateValue : null;
        if (!long.TryParse(Required(parameters, "vnp_Amount"), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
            throw InvalidPayload();
        return ToVerifiedEvent(txnRef, FromVnpayAmount(amount), responseCode, transactionStatus, transactionNo, payDate, receivedAt);
    }

    private static VerifiedPaymentEvent ToVerifiedEvent(
        string txnRef,
        long amountMinor,
        string responseCode,
        string transactionStatus,
        string transactionNo,
        string? payDate,
        DateTimeOffset receivedAt)
    {
        if (!TryParseVnpayTxnRef(txnRef, out var orderId)) throw InvalidPayload();
        var occurredAt = TryParsePayDate(payDate, out var parsed) ? parsed : receivedAt;
        var isPaid = responseCode == "00" && transactionStatus == "00";
        var isFinal = isPaid || responseCode != "00" || transactionStatus != "01";
        return new VerifiedPaymentEvent(
            $"{Provider}:{txnRef}:{transactionNo}:{responseCode}:{transactionStatus}",
            orderId,
            txnRef,
            amountMinor,
            "VND",
            isPaid,
            isFinal,
            occurredAt);
    }

    private void VerifyQueryResponseSignature(VnpayQueryResponse payload)
    {
        if (string.IsNullOrWhiteSpace(payload.SecureHash)) throw InvalidResponse();
        var rawHash = string.Join('|',
            payload.ResponseId,
            payload.Command,
            payload.ResponseCode,
            payload.Message,
            payload.TmnCode,
            payload.TxnRef,
            payload.Amount.ToString(CultureInfo.InvariantCulture),
            payload.BankCode ?? string.Empty,
            payload.PayDate ?? string.Empty,
            payload.TransactionNo ?? string.Empty,
            payload.TransactionType ?? string.Empty,
            payload.TransactionStatus,
            payload.OrderInfo,
            payload.PromotionCode ?? string.Empty,
            payload.PromotionAmount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
        var expected = SignSha512(rawHash, _options.HashSecret);
        if (!FixedEquals(payload.SecureHash, expected)) throw InvalidResponse();
    }

    private static bool TryParsePayDate(string? payDate, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(payDate)) return false;
        if (!DateTime.TryParseExact(payDate, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var localTime))
            return false;
        value = new DateTimeOffset(localTime, VietnamOffset).ToUniversalTime();
        return true;
    }

    private static string Required(IReadOnlyDictionary<string, string> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) throw InvalidPayload();
        return value.Trim();
    }

    private static string OrderInfo(string txnRef) => $"Nexora order {txnRef}";

    private static string NormalizeIpAddress(string? ipAddress) =>
        string.IsNullOrWhiteSpace(ipAddress) ? "127.0.0.1" : ipAddress.Trim()[..Math.Min(ipAddress.Trim().Length, 45)];

    private static string NormalizeLocale(string? locale) =>
        string.Equals(locale, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "vn";

    private static string NormalizeOrderType(string? orderType) =>
        string.IsNullOrWhiteSpace(orderType) ? "other" : new string(orderType.Where(char.IsLetterOrDigit).Take(100).ToArray());

    private static void ValidateOrderRequest(PaymentOrderRequest request)
    {
        if (!string.Equals(request.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_CURRENCY_NOT_SUPPORTED", "VNPAY sandbox chỉ hỗ trợ VND.", BusinessErrorKind.Validation);
        if (request.AmountMinor is <= 0 or > 99_999_999)
            throw new BusinessException("PAYMENT_AMOUNT_NOT_SUPPORTED", "Số tiền VNPAY sandbox không hợp lệ.", BusinessErrorKind.Validation);
        if (!string.Equals(request.ProviderTransactionId, ToVnpayTxnRef(request.OrderId), StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán không khớp order.", BusinessErrorKind.Validation);
    }

    private static void ValidatePaymentEndpoint(string paymentUrl)
    {
        if (!Uri.TryCreate(paymentUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "sandbox.vnpayment.vn", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, "/paymentv2/vpcpay.html", StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_PROVIDER_INVALID_CONFIG", "VNPAY sandbox payment URL không hợp lệ.", BusinessErrorKind.ExternalFailure);
    }

    private static void ValidateQueryEndpoint(string queryUrl)
    {
        if (!Uri.TryCreate(queryUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "sandbox.vnpayment.vn", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, "/merchant_webapi/api/transaction", StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_PROVIDER_INVALID_CONFIG", "VNPAY sandbox query URL không hợp lệ.", BusinessErrorKind.ExternalFailure);
    }

    private static bool FixedEquals(string supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim().ToLowerInvariant());
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static string Encode(string value) => WebUtility.UrlEncode(value) ?? string.Empty;

    private static BusinessException InvalidWebhook() =>
        new("INVALID_WEBHOOK_SIGNATURE", "Webhook thanh toán không hợp lệ.", BusinessErrorKind.Unauthorized);

    private static BusinessException InvalidPayload() =>
        new("INVALID_WEBHOOK_PAYLOAD", "Dữ liệu webhook thanh toán không hợp lệ.", BusinessErrorKind.Validation);

    private static BusinessException InvalidResponse() =>
        new("PAYMENT_PROVIDER_INVALID_RESPONSE", "VNPAY sandbox trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);

    private static BusinessException QueryFailed() =>
        new("PAYMENT_PROVIDER_QUERY_FAILED", "Không thể tra cứu trạng thái giao dịch VNPAY.", BusinessErrorKind.ExternalFailure);

    private sealed record VnpayQueryRequest(
        [property: JsonPropertyName("vnp_RequestId")] string RequestId,
        [property: JsonPropertyName("vnp_Version")] string Version,
        [property: JsonPropertyName("vnp_Command")] string Command,
        [property: JsonPropertyName("vnp_TmnCode")] string TmnCode,
        [property: JsonPropertyName("vnp_TxnRef")] string TxnRef,
        [property: JsonPropertyName("vnp_OrderInfo")] string OrderInfo,
        [property: JsonPropertyName("vnp_TransactionDate")] string TransactionDate,
        [property: JsonPropertyName("vnp_CreateDate")] string CreateDate,
        [property: JsonPropertyName("vnp_IpAddr")] string IpAddress,
        [property: JsonPropertyName("vnp_SecureHash")] string SecureHash);

    private sealed record VnpayQueryResponse(
        [property: JsonPropertyName("vnp_ResponseId")] string ResponseId,
        [property: JsonPropertyName("vnp_Command")] string Command,
        [property: JsonPropertyName("vnp_ResponseCode")] string ResponseCode,
        [property: JsonPropertyName("vnp_Message")] string Message,
        [property: JsonPropertyName("vnp_TmnCode")] string TmnCode,
        [property: JsonPropertyName("vnp_TxnRef")] string TxnRef,
        [property: JsonPropertyName("vnp_Amount")] long Amount,
        [property: JsonPropertyName("vnp_BankCode")] string? BankCode,
        [property: JsonPropertyName("vnp_PayDate")] string? PayDate,
        [property: JsonPropertyName("vnp_TransactionNo")] string? TransactionNo,
        [property: JsonPropertyName("vnp_TransactionType")] string? TransactionType,
        [property: JsonPropertyName("vnp_TransactionStatus")] string TransactionStatus,
        [property: JsonPropertyName("vnp_OrderInfo")] string OrderInfo,
        [property: JsonPropertyName("vnp_PromotionCode")] string? PromotionCode,
        [property: JsonPropertyName("vnp_PromotionAmount")] long? PromotionAmount,
        [property: JsonPropertyName("vnp_SecureHash")] string SecureHash);
}
