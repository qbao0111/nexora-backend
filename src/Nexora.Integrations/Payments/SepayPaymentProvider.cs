using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Common;

namespace Nexora.Integrations.Payments;

public sealed class SepayOptions
{
    public const string SectionName = "Billing:Sepay";
    public string Environment { get; init; } = "Sandbox";
    public string MerchantId { get; init; } = string.Empty;
    public string SecretKey { get; init; } = string.Empty;
    public string CheckoutUrl { get; init; } = "https://pay-sandbox.sepay.vn/v1/checkout/init";
    public string ApiBaseUrl { get; init; } = "https://pgapi-sandbox.sepay.vn";
    public string PaymentMethod { get; init; } = string.Empty;
    public string SuccessUrl { get; init; } = string.Empty;
    public string ErrorUrl { get; init; } = string.Empty;
    public string CancelUrl { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 35;
}

public sealed class SepayPaymentProvider(HttpClient httpClient, IOptions<SepayOptions> options, TimeProvider timeProvider) : IPaymentProvider
{
    private const string Provider = "sepay";
    private const string SandboxCheckoutHost = "pay-sandbox.sepay.vn";
    private const string SandboxCheckoutPath = "/v1/checkout/init";
    private const string SandboxApiHost = "pgapi-sandbox.sepay.vn";
    private const int MaximumIdentifierLength = 160;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] OrderedFieldNames =
    [
        "order_amount", "merchant", "currency", "operation", "order_description", "order_invoice_number",
        "customer_id", "payment_method", "success_url", "error_url", "cancel_url"
    ];
    private readonly SepayOptions _options = options.Value;

    public string ProviderName => Provider;

    public string CreateProviderTransactionId(Guid orderId) => ToSepayInvoiceNumber(orderId);

    public Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOrderRequest(request);
        ValidateCheckoutEndpoint(_options.CheckoutUrl);

        var fields = new List<CheckoutFormField>
        {
            new("order_amount", request.AmountMinor.ToString(CultureInfo.InvariantCulture)),
            new("merchant", _options.MerchantId),
            new("currency", "VND"),
            new("operation", "PURCHASE"),
            new("order_description", $"Nexora order {request.ProviderTransactionId}"),
            new("order_invoice_number", request.ProviderTransactionId)
        };
        AddOptionalField(fields, "payment_method", _options.PaymentMethod);
        AddOptionalField(fields, "success_url", _options.SuccessUrl);
        AddOptionalField(fields, "error_url", _options.ErrorUrl);
        AddOptionalField(fields, "cancel_url", _options.CancelUrl);
        fields.Add(new CheckoutFormField("signature", SignFields(fields, _options.SecretKey)));

        return Task.FromResult(new PaymentCheckout(
            Provider,
            request.ProviderTransactionId,
            new CheckoutAction("POST", _options.CheckoutUrl, fields.ToArray())));
    }

    public async Task<VerifiedPaymentEvent> VerifyWebhookAsync(PaymentCallbackRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Method, HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase))
            throw InvalidPayload();
        if (!request.Headers.TryGetValue("X-Secret-Key", out var secret) || !FixedEquals(secret, _options.SecretKey))
            throw InvalidWebhook();

        SepayIpnPayload? payload;
        try { payload = JsonSerializer.Deserialize<SepayIpnPayload>(request.Body.Span, JsonOptions); }
        catch (JsonException) { throw InvalidPayload(); }
        if (payload is null) throw InvalidPayload();

        var notificationType = Required(payload.NotificationType).ToUpperInvariant();
        if (notificationType is not ("ORDER_PAID" or "TRANSACTION_VOID")) throw InvalidPayload();
        var order = payload.Order ?? throw InvalidPayload();
        var transaction = payload.Transaction ?? throw InvalidPayload();
        var providerOrderId = Required(ReadValue(order.OrderId));
        var invoice = Required(ReadValue(order.InvoiceNumber));
        var transactionId = Required(ReadValue(transaction.TransactionId));
        var transactionRecordId = Required(ReadValue(transaction.Id));
        if (providerOrderId.Length > MaximumIdentifierLength || transactionId.Length > MaximumIdentifierLength ||
            transactionRecordId.Length > MaximumIdentifierLength)
            throw InvalidPayload();
        if (!TryParseSepayInvoiceNumber(invoice, out var orderId) ||
            !string.Equals(invoice, ToSepayInvoiceNumber(orderId), StringComparison.Ordinal))
            throw InvalidPayload();

        var orderStatus = Required(ReadValue(order.OrderStatus)).ToUpperInvariant();
        var transactionStatus = Required(ReadValue(transaction.TransactionStatus)).ToUpperInvariant();
        var orderCurrency = Required(ReadValue(order.OrderCurrency)).ToUpperInvariant();
        var transactionCurrency = Required(ReadValue(transaction.TransactionCurrency)).ToUpperInvariant();
        if (orderCurrency != "VND" || transactionCurrency != "VND") throw InvalidPayload();
        var orderAmount = ParseAmount(ReadValue(order.OrderAmount));
        var transactionAmount = ParseAmount(ReadValue(transaction.TransactionAmount));
        if (orderAmount != transactionAmount) throw InvalidPayload();
        if (!TryReadUnixTimestamp(payload.Timestamp, out var occurredAt)) throw InvalidPayload();
        if (notificationType == "ORDER_PAID" && (orderStatus != "CAPTURED" || transactionStatus != "APPROVED"))
            throw InvalidPayload();

        var eventId = $"{Provider}:ipn:{notificationType}:{providerOrderId}:{transactionRecordId}:{transactionId}";
        if (eventId.Length > MaximumIdentifierLength) throw InvalidPayload();
        return new VerifiedPaymentEvent(
            eventId,
            orderId,
            invoice,
            orderAmount,
            "VND",
            notificationType == "ORDER_PAID",
            true,
            occurredAt);
    }

    public async Task<VerifiedPaymentEvent?> QueryPaymentAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        ValidateOrderRequest(request);
        ValidateApiEndpoint(_options.ApiBaseUrl);
        var query = $"per_page=100&page=1&q={Uri.EscapeDataString(request.ProviderTransactionId)}";
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl.TrimEnd('/')}/v1/order?{query}");
        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.MerchantId}:{_options.SecretKey}"));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        HttpResponseMessage response;
        try { response = await httpClient.SendAsync(httpRequest, cancellationToken); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw ProviderUnavailable(); }
        catch (HttpRequestException) { throw ProviderUnavailable(); }
        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new BusinessException("PAYMENT_PROVIDER_AUTH_FAILED", "Không thể xác thực với cổng thanh toán.", BusinessErrorKind.ExternalFailure);
            if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                throw ProviderUnavailable();
            if (!response.IsSuccessStatusCode) throw InvalidResponse();

            SepayOrderListResponse? result;
            try { result = await response.Content.ReadFromJsonAsync<SepayOrderListResponse>(JsonOptions, cancellationToken); }
            catch (JsonException) { throw InvalidResponse(); }
            if (result?.Data is null) throw InvalidResponse();
            var matches = result.Data.Where(item => string.Equals(ReadValue(item.OrderInvoiceNumber), request.ProviderTransactionId, StringComparison.Ordinal)).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length > 1) throw InvalidResponse();
            var order = matches[0];
            var currency = Required(ReadValue(order.OrderCurrency)).ToUpperInvariant();
            if (currency != "VND") throw new BusinessException("PAYMENT_CURRENCY_NOT_SUPPORTED", "SePay chỉ hỗ trợ VND.", BusinessErrorKind.Validation);
            var amount = ParseAmount(ReadValue(order.OrderAmount));
            if (amount != request.AmountMinor)
                throw new BusinessException("PAYMENT_AMOUNT_MISMATCH", "Số tiền thanh toán không khớp order.", BusinessErrorKind.Validation);
            var status = Required(ReadValue(order.OrderStatus)).ToUpperInvariant();
            var providerOrderId = Required(ReadValue(order.OrderId));
            if (status == "AUTHENTICATION_NOT_NEEDED") return null;
            if (status is not ("CAPTURED" or "CANCELLED")) throw QueryFailed();
            var eventId = $"{Provider}:query:{providerOrderId}:{status}";
            if (eventId.Length > MaximumIdentifierLength) throw InvalidResponse();
            return new VerifiedPaymentEvent(eventId, ParseOrderId(request.ProviderTransactionId), request.ProviderTransactionId, amount, "VND", status == "CAPTURED", true, timeProvider.GetUtcNow());
        }
    }

    public static string ToSepayInvoiceNumber(Guid orderId) => $"NX{orderId:N}";

    public static bool TryParseSepayInvoiceNumber(string? value, out Guid orderId)
    {
        orderId = Guid.Empty;
        return value is { Length: 34 } && value.StartsWith("NX", StringComparison.OrdinalIgnoreCase) &&
            value[2..].All(Uri.IsHexDigit) && Guid.TryParseExact(value[2..], "N", out orderId);
    }

    public static string SignFields(IReadOnlyList<CheckoutFormField> fields, string secret)
    {
        var signing = string.Join(',', OrderedFieldNames.SelectMany(name => fields
                .Where(field => string.Equals(field.Name, name, StringComparison.Ordinal)))
            .Select(field => $"{field.Name}={field.Value}"));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signing)));
    }

    private static Guid ParseOrderId(string invoice) =>
        TryParseSepayInvoiceNumber(invoice, out var orderId) ? orderId : throw InvalidPayload();

    private static void AddOptionalField(List<CheckoutFormField> fields, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) fields.Add(new CheckoutFormField(name, value));
    }

    private static string ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        _ => string.Empty
    };

    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw InvalidPayload() : value.Trim();

    private static long ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !decimal.TryParse(value.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount) ||
            amount <= 0 || decimal.Truncate(amount) != amount || amount > long.MaxValue)
            throw InvalidPayload();
        return (long)amount;
    }

    private static bool TryReadUnixTimestamp(JsonElement value, out DateTimeOffset timestamp)
    {
        timestamp = default;
        long seconds;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out seconds)) { }
        else if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)) { }
        else return false;
        try { timestamp = DateTimeOffset.FromUnixTimeSeconds(seconds); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static bool FixedEquals(string supplied, string expected)
    {
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim());
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return suppliedBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static void ValidateOrderRequest(PaymentOrderRequest request)
    {
        if (!string.Equals(request.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_CURRENCY_NOT_SUPPORTED", "SePay chỉ hỗ trợ VND.", BusinessErrorKind.Validation);
        if (request.AmountMinor <= 0)
            throw new BusinessException("PAYMENT_AMOUNT_NOT_SUPPORTED", "Số tiền thanh toán không hợp lệ.", BusinessErrorKind.Validation);
        if (!string.Equals(request.ProviderTransactionId, ToSepayInvoiceNumber(request.OrderId), StringComparison.Ordinal))
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán không khớp order.", BusinessErrorKind.Validation);
    }

    private static void ValidateCheckoutEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, SandboxCheckoutHost, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.AbsolutePath, SandboxCheckoutPath, StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.Query))
            throw new BusinessException("PAYMENT_PROVIDER_INVALID_CONFIG", "SePay sandbox checkout URL không hợp lệ.", BusinessErrorKind.ExternalFailure);
    }

    private static void ValidateApiEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, SandboxApiHost, StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath != "/" && uri.AbsolutePath != string.Empty)
            throw new BusinessException("PAYMENT_PROVIDER_INVALID_CONFIG", "SePay sandbox API URL không hợp lệ.", BusinessErrorKind.ExternalFailure);
    }

    private static BusinessException InvalidWebhook() =>
        new("INVALID_WEBHOOK_SIGNATURE", "Webhook thanh toán không hợp lệ.", BusinessErrorKind.Unauthorized);

    private static BusinessException InvalidPayload() =>
        new("INVALID_WEBHOOK_PAYLOAD", "Dữ liệu webhook thanh toán không hợp lệ.", BusinessErrorKind.Validation);

    private static BusinessException InvalidResponse() =>
        new("PAYMENT_PROVIDER_INVALID_RESPONSE", "Cổng thanh toán trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);

    private static BusinessException ProviderUnavailable() =>
        new("PAYMENT_PROVIDER_UNAVAILABLE", "Cổng thanh toán hiện không khả dụng.", BusinessErrorKind.ExternalFailure);

    private static BusinessException QueryFailed() =>
        new("PAYMENT_PROVIDER_QUERY_FAILED", "Không thể tra cứu trạng thái giao dịch.", BusinessErrorKind.ExternalFailure);

    private sealed record SepayIpnPayload(
        JsonElement Timestamp,
        [property: JsonPropertyName("notification_type")] string? NotificationType,
        SepayIpnOrder? Order,
        SepayIpnTransaction? Transaction,
        JsonElement Customer);

    private sealed record SepayIpnOrder(
        [property: JsonPropertyName("order_id")] JsonElement OrderId,
        [property: JsonPropertyName("order_status")] JsonElement OrderStatus,
        [property: JsonPropertyName("order_currency")] JsonElement OrderCurrency,
        [property: JsonPropertyName("order_amount")] JsonElement OrderAmount,
        [property: JsonPropertyName("order_invoice_number")] JsonElement InvoiceNumber);

    private sealed record SepayIpnTransaction(
        JsonElement Id,
        [property: JsonPropertyName("transaction_id")] JsonElement TransactionId,
        [property: JsonPropertyName("transaction_status")] JsonElement TransactionStatus,
        [property: JsonPropertyName("transaction_amount")] JsonElement TransactionAmount,
        [property: JsonPropertyName("transaction_currency")] JsonElement TransactionCurrency);

    private sealed record SepayOrderListResponse([property: JsonPropertyName("data")] SepayOrder[]? Data);

    private sealed record SepayOrder(
        [property: JsonPropertyName("order_id")] JsonElement OrderId,
        [property: JsonPropertyName("order_status")] JsonElement OrderStatus,
        [property: JsonPropertyName("order_currency")] JsonElement OrderCurrency,
        [property: JsonPropertyName("order_amount")] JsonElement OrderAmount,
        [property: JsonPropertyName("order_invoice_number")] JsonElement OrderInvoiceNumber);
}
