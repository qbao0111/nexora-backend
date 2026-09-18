using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nexora.Business.Billing;
using Nexora.Business.Common;
using PayOS;
using PayOS.Exceptions;
using PayOS.Models;
using PayOS.Models.V2.PaymentRequests;
using PayOS.Models.Webhooks;

namespace Nexora.Integrations.Payments;

public sealed class PayosPaymentProvider : IPaymentProvider, IDisposable
{
    private const string Provider = "payos";
    private const string PaymentDescription = "Nexora";
    private const long MinimumGeneratedOrderCode = 1_000_000_000_000_000;
    private const long MaximumGeneratedOrderCodeExclusive = 9_000_000_000_000_000;
    private const long MaximumOrderCode = 9_007_199_254_740_991;
    private const int MaximumIdentifierLength = 120;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PayosOptions _options;
    private readonly PayOSClient _client;
    private readonly TimeProvider _timeProvider;

    public PayosPaymentProvider(HttpClient httpClient, IOptions<PayosOptions> options, TimeProvider timeProvider)
    {
        _options = options.Value;
        _timeProvider = timeProvider;
        _client = new PayOSClient(new PayOS.PayOSOptions
        {
            ClientId = _options.ClientId,
            ApiKey = _options.ApiKey,
            ChecksumKey = _options.ChecksumKey,
            HttpClient = httpClient,
            TimeoutMs = checked(_options.TimeoutSeconds * 1_000),
            MaxRetries = 0,
            LogLevel = Microsoft.Extensions.Logging.LogLevel.None
        });
    }

    public string ProviderName => Provider;

    public string CreateProviderTransactionId(Guid orderId)
    {
        _ = orderId;
        return GenerateOrderCode().ToString(CultureInfo.InvariantCulture);
    }

    public CheckoutAction? RestoreCheckoutAction(string checkoutUrl) =>
        IsValidCheckoutUrl(checkoutUrl)
            ? new CheckoutAction("GET", checkoutUrl, Array.Empty<CheckoutFormField>())
            : null;

    public void Dispose() => _client.Dispose();

    public async Task<PaymentCheckout> CreateCheckoutAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        ValidateOrderRequest(request);
        var orderCode = ParseOrderCode(request.ProviderTransactionId);
        CreatePaymentLinkResponse response;
        try
        {
            response = await _client.PaymentRequests.CreateAsync(
                new CreatePaymentLinkRequest
                {
                    OrderCode = orderCode,
                    Amount = request.AmountMinor,
                    Description = PaymentDescription,
                    ReturnUrl = _options.ReturnUrl,
                    CancelUrl = _options.CancelUrl
                },
                new RequestOptions<CreatePaymentLinkRequest>
                {
                    CancellationToken = cancellationToken,
                    MaxRetries = 0
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ProviderUnavailable();
        }
        catch (HttpRequestException)
        {
            throw ProviderUnavailable();
        }
        catch (ApiException exception)
        {
            throw MapCheckoutException(exception);
        }
        catch (PayOSException)
        {
            throw CheckoutFailed();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }

        ValidateCheckoutResponse(response, request, orderCode);
        return new PaymentCheckout(
            Provider,
            request.ProviderTransactionId,
            new CheckoutAction("GET", response.CheckoutUrl, Array.Empty<CheckoutFormField>()));
    }

    public async Task<VerifiedPaymentEvent> VerifyWebhookAsync(PaymentCallbackRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(request.Method, HttpMethod.Post.Method, StringComparison.OrdinalIgnoreCase))
            throw InvalidPayload();

        Webhook? webhook;
        try
        {
            webhook = JsonSerializer.Deserialize<Webhook>(request.Body.Span, JsonOptions);
        }
        catch (JsonException)
        {
            throw InvalidPayload();
        }
        if (webhook is null) throw InvalidPayload();

        WebhookData data;
        try
        {
            data = await _client.Webhooks.VerifyAsync(webhook);
        }
        catch (WebhookException)
        {
            throw InvalidWebhook();
        }
        catch (PayOSException)
        {
            throw InvalidWebhook();
        }

        if (data is null || !webhook.Success || !IsSuccessCode(webhook.Code) || !IsSuccessCode(data.Code))
            throw InvalidPayload();
        if (!IsValidOrderCode(data.OrderCode) || data.Amount <= 0 ||
            !string.Equals(data.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            throw InvalidPayload();

        var paymentLinkId = RequiredIdentifier(data.PaymentLinkId);
        var reference = RequiredIdentifier(data.Reference);
        if (!TryParseWebhookTimestamp(data.TransactionDateTime, out var occurredAt)) throw InvalidPayload();

        var orderCode = data.OrderCode.ToString(CultureInfo.InvariantCulture);
        if (data.OrderCode < MinimumGeneratedOrderCode)
        {
            return new VerifiedPaymentEvent(
                BuildProviderEventId("webhook", orderCode, paymentLinkId, reference),
                null,
                orderCode,
                data.Amount,
                "VND",
                false,
                false,
                occurredAt,
                true);
        }

        var paymentLink = await GetPaymentLinkForWebhookAsync(data.OrderCode, cancellationToken);
        ValidateWebhookPaymentLink(paymentLink, data, paymentLinkId);

        var paidAmountMatchesLink = paymentLink.AmountPaid == paymentLink.Amount;
        var isPaid = paymentLink.Status == PaymentLinkStatus.Paid && paidAmountMatchesLink;
        var isFinal = isPaid || paymentLink.Status is PaymentLinkStatus.Cancelled or PaymentLinkStatus.Expired or PaymentLinkStatus.Failed;
        return new VerifiedPaymentEvent(
            BuildProviderEventId("webhook", orderCode, paymentLinkId, reference),
            null,
            orderCode,
            paymentLink.Amount,
            "VND",
            isPaid,
            isFinal,
            occurredAt,
            false);
    }

    public async Task<VerifiedPaymentEvent?> QueryPaymentAsync(PaymentOrderRequest request, CancellationToken cancellationToken)
    {
        ValidateOrderRequest(request);
        var orderCode = ParseOrderCode(request.ProviderTransactionId);
        PaymentLink paymentLink;
        try
        {
            paymentLink = await _client.PaymentRequests.GetAsync(
                orderCode,
                new RequestOptions
                {
                    CancellationToken = cancellationToken,
                    MaxRetries = 0
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ProviderUnavailable();
        }
        catch (HttpRequestException)
        {
            throw ProviderUnavailable();
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            return null;
        }
        catch (ApiException exception)
        {
            throw MapQueryException(exception);
        }
        catch (PayOSException)
        {
            throw QueryFailed();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }

        ValidateQueryResponse(paymentLink, request, orderCode);
        return paymentLink.Status switch
        {
            PaymentLinkStatus.Pending or PaymentLinkStatus.Processing or PaymentLinkStatus.Underpaid => null,
            PaymentLinkStatus.Paid => CreateQueryEvent(paymentLink, request, isPaid: true),
            PaymentLinkStatus.Cancelled or PaymentLinkStatus.Expired or PaymentLinkStatus.Failed =>
                CreateQueryEvent(paymentLink, request, isPaid: false),
            _ => throw QueryFailed()
        };
    }

    private VerifiedPaymentEvent CreateQueryEvent(PaymentLink paymentLink, PaymentOrderRequest request, bool isPaid)
    {
        if (isPaid && paymentLink.AmountPaid != request.AmountMinor)
            throw AmountMismatch();

        var status = paymentLink.Status.ToString().ToUpperInvariant();
        return new VerifiedPaymentEvent(
            BuildProviderEventId("query", request.ProviderTransactionId, paymentLink.Id, status),
            request.OrderId,
            request.ProviderTransactionId,
            request.AmountMinor,
            "VND",
            isPaid,
            true,
            _timeProvider.GetUtcNow());
    }

    private static void ValidateOrderRequest(PaymentOrderRequest request)
    {
        if (!string.Equals(request.Currency, "VND", StringComparison.OrdinalIgnoreCase))
            throw new BusinessException("PAYMENT_CURRENCY_NOT_SUPPORTED", "payOS chỉ hỗ trợ VND.", BusinessErrorKind.Validation);
        if (request.AmountMinor <= 0)
            throw new BusinessException("PAYMENT_AMOUNT_NOT_SUPPORTED", "Số tiền thanh toán không hợp lệ.", BusinessErrorKind.Validation);
        if (!TryParseOrderCode(request.ProviderTransactionId, out _))
            throw new BusinessException("PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán không khớp order.", BusinessErrorKind.Validation);
    }

    private static void ValidateCheckoutResponse(CreatePaymentLinkResponse? response, PaymentOrderRequest request, long orderCode)
    {
        if (response is null || response.OrderCode != orderCode || response.Amount != request.AmountMinor ||
            !string.Equals(response.Currency, "VND", StringComparison.OrdinalIgnoreCase) ||
            response.Status is not (PaymentLinkStatus.Pending or PaymentLinkStatus.Processing) ||
            !IsValidIdentifier(response.PaymentLinkId) || !IsValidCheckoutUrl(response.CheckoutUrl))
            throw InvalidResponse();
    }

    private static void ValidateQueryResponse(PaymentLink? response, PaymentOrderRequest request, long orderCode)
    {
        if (response is null || response.OrderCode != orderCode || !IsValidIdentifier(response.Id))
            throw InvalidResponse();
        if (response.Amount != request.AmountMinor)
            throw AmountMismatch();
    }

    private async Task<PaymentLink> GetPaymentLinkForWebhookAsync(long orderCode, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.PaymentRequests.GetAsync(
                orderCode,
                new RequestOptions
                {
                    CancellationToken = cancellationToken,
                    MaxRetries = 0
                });
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw ProviderUnavailable();
        }
        catch (HttpRequestException)
        {
            throw ProviderUnavailable();
        }
        catch (ApiException exception) when (exception.StatusCode == 404)
        {
            throw InvalidPayload();
        }
        catch (ApiException exception)
        {
            throw MapQueryException(exception);
        }
        catch (PayOSException)
        {
            throw QueryFailed();
        }
        catch (JsonException)
        {
            throw InvalidResponse();
        }
    }

    private static void ValidateWebhookPaymentLink(PaymentLink? response, WebhookData data, string paymentLinkId)
    {
        if (response is null || response.OrderCode != data.OrderCode ||
            !IsValidIdentifier(response.Id) ||
            !string.Equals(response.Id, paymentLinkId, StringComparison.Ordinal) ||
            response.Amount <= 0 || response.AmountPaid < 0 || response.AmountRemaining < 0)
            throw InvalidResponse();
    }

    private static bool IsSuccessCode(string? value) => string.Equals(value, "00", StringComparison.Ordinal);

    private static long GenerateOrderCode()
    {
        var range = (ulong)(MaximumGeneratedOrderCodeExclusive - MinimumGeneratedOrderCode);
        var rejectionLimit = ulong.MaxValue - ulong.MaxValue % range;
        Span<byte> randomBytes = stackalloc byte[sizeof(ulong)];
        while (true)
        {
            RandomNumberGenerator.Fill(randomBytes);
            var randomValue = BinaryPrimitives.ReadUInt64LittleEndian(randomBytes);
            if (randomValue >= rejectionLimit) continue;
            return checked(MinimumGeneratedOrderCode + (long)(randomValue % range));
        }
    }

    private static bool TryParseOrderCode(string? value, out long orderCode)
    {
        orderCode = 0;
        return value is { Length: > 0 and <= 16 } &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out orderCode) &&
            IsValidOrderCode(orderCode) &&
            string.Equals(value, orderCode.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static long ParseOrderCode(string value) =>
        TryParseOrderCode(value, out var orderCode) ? orderCode : throw new BusinessException(
            "PAYMENT_REFERENCE_MISMATCH", "Thông tin thanh toán không khớp order.", BusinessErrorKind.Validation);

    private static bool IsValidOrderCode(long orderCode) => orderCode > 0 && orderCode <= MaximumOrderCode;

    private static string RequiredIdentifier(string? value) =>
        IsValidIdentifier(value) ? value!.Trim() : throw InvalidPayload();

    private static bool IsValidIdentifier(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= MaximumIdentifierLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(char.IsControl);

    private static bool IsValidCheckoutUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri is not null &&
        uri.Scheme == Uri.UriSchemeHttps &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool TryParseWebhookTimestamp(string? value, out DateTimeOffset occurredAt)
    {
        occurredAt = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var timestamp = value.Trim();
        if (DateTimeOffset.TryParseExact(
                timestamp,
                ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out occurredAt))
            return true;
        if (!DateTime.TryParseExact(
                timestamp,
                ["yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var localTimestamp))
            return false;

        occurredAt = new DateTimeOffset(
            DateTime.SpecifyKind(localTimestamp, DateTimeKind.Unspecified),
            TimeSpan.FromHours(7)).ToUniversalTime();
        return true;
    }

    private static string BuildProviderEventId(string kind, params string[] values)
    {
        var material = string.Join("\n", values);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
        return $"{Provider}:{kind}:{hash}";
    }

    private static BusinessException MapCheckoutException(ApiException exception)
    {
        var statusCode = exception.StatusCode.GetValueOrDefault();
        if (statusCode is 401 or 403)
            return new BusinessException("PAYMENT_PROVIDER_AUTH_FAILED", "Không thể xác thực với cổng thanh toán.", BusinessErrorKind.ExternalFailure);
        if (statusCode == 429 || statusCode >= 500)
            return ProviderUnavailable();
        return CheckoutFailed();
    }

    private static BusinessException MapQueryException(ApiException exception)
    {
        var statusCode = exception.StatusCode.GetValueOrDefault();
        if (statusCode is 401 or 403)
            return new BusinessException("PAYMENT_PROVIDER_AUTH_FAILED", "Không thể xác thực với cổng thanh toán.", BusinessErrorKind.ExternalFailure);
        if (statusCode == 429 || statusCode >= 500)
            return ProviderUnavailable();
        return QueryFailed();
    }

    private static BusinessException InvalidWebhook() =>
        new("INVALID_WEBHOOK_SIGNATURE", "Webhook thanh toán không hợp lệ.", BusinessErrorKind.Unauthorized);

    private static BusinessException InvalidPayload() =>
        new("INVALID_WEBHOOK_PAYLOAD", "Dữ liệu webhook thanh toán không hợp lệ.", BusinessErrorKind.Validation);

    private static BusinessException InvalidResponse() =>
        new("PAYMENT_PROVIDER_INVALID_RESPONSE", "Cổng thanh toán trả về dữ liệu không hợp lệ.", BusinessErrorKind.ExternalFailure);

    private static BusinessException AmountMismatch() =>
        new("PAYMENT_AMOUNT_MISMATCH", "Số tiền thanh toán không khớp order.", BusinessErrorKind.Validation);

    private static BusinessException CheckoutFailed() =>
        new("PAYMENT_CHECKOUT_FAILED", "Không thể tạo phiên thanh toán.", BusinessErrorKind.ExternalFailure);

    private static BusinessException ProviderUnavailable() =>
        new("PAYMENT_PROVIDER_UNAVAILABLE", "Cổng thanh toán hiện không khả dụng.", BusinessErrorKind.ExternalFailure);

    private static BusinessException QueryFailed() =>
        new("PAYMENT_PROVIDER_QUERY_FAILED", "Không thể tra cứu trạng thái giao dịch.", BusinessErrorKind.ExternalFailure);
}
