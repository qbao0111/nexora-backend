using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Integrations.Ai;
using Nexora.Integrations.Email;
using Nexora.Integrations.Payments;
using Nexora.Integrations.Speech;
using Nexora.Integrations.Storage;

namespace Nexora.Integrations;

public static class DependencyInjection
{
    private const int MaximumSepayCallbackUrlLength = 2048;
    private static readonly string[] AllowedSepayPaymentMethods =
    [
        "CARD",
        "BANK_TRANSFER",
        "NAPAS_BANK_TRANSFER"
    ];

    public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddEmail(configuration);
        services.AddSpeech(configuration);
        var storageProvider = configuration.GetValue<string?>($"{StorageOptions.SectionName}:Provider")?.Trim().ToLowerInvariant() ?? "local";
        if (storageProvider is not ("local" or "r2"))
            throw new InvalidOperationException("Storage:Provider must be local or r2.");

        var r2Enabled = string.Equals(storageProvider, "r2", StringComparison.Ordinal);
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(options => options.Provider?.Trim().ToLowerInvariant() is "local" or "r2",
                "Storage:Provider must be local or r2.")
            .ValidateOnStart();
        services.AddOptions<R2StorageOptions>().Bind(configuration.GetSection(R2StorageOptions.SectionName))
            .Validate(options => !r2Enabled || !string.IsNullOrWhiteSpace(options.AccountId),
                "Storage:R2:AccountId is required when Storage:Provider=r2.")
            .Validate(options => !r2Enabled || !string.IsNullOrWhiteSpace(options.Bucket),
                "Storage:R2:Bucket is required when Storage:Provider=r2.")
            .Validate(options => !r2Enabled || !string.IsNullOrWhiteSpace(options.AccessKeyId),
                "Storage:R2:AccessKeyId is required when Storage:Provider=r2.")
            .Validate(options => !r2Enabled || !string.IsNullOrWhiteSpace(options.SecretAccessKey),
                "Storage:R2:SecretAccessKey is required when Storage:Provider=r2.")
            .Validate(options => !r2Enabled || R2ConfigurationValidation.IsValidEndpoint(options.Endpoint),
                "Storage:R2:Endpoint must be an absolute HTTPS URL without credentials, query or fragment when Storage:Provider=r2.")
            .ValidateOnStart();
        services.AddOptions<LocalStorageOptions>().Bind(configuration.GetSection(LocalStorageOptions.SectionName))
            .Validate(options => r2Enabled || !string.IsNullOrWhiteSpace(options.RootPath), "Storage:Local:RootPath is required when Storage:Provider=local.")
            .ValidateOnStart();
        services.AddSingleton<LocalStorageProvider>();
        if (r2Enabled)
        {
            services.AddSingleton<IR2ObjectClient, R2ObjectClient>();
            services.AddSingleton<R2StorageProvider>(provider => new R2StorageProvider(
                provider.GetRequiredService<IOptions<R2StorageOptions>>(),
                provider.GetRequiredService<IR2ObjectClient>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetService<ILogger<R2StorageProvider>>()));
        }
        services.AddSingleton<IStorageProvider>(provider => r2Enabled
            ? provider.GetRequiredService<R2StorageProvider>()
            : provider.GetRequiredService<LocalStorageProvider>());
        services.AddOptions<UploadOptions>().Bind(configuration.GetSection(UploadOptions.SectionName))
            .Validate(options => options.MaxResumeBytes is > 0 and <= 25 * 1024 * 1024, "Upload limit must be between 1 byte and 25 MiB.")
            .Validate(options => options.IntentMinutes is > 0 and <= 60, "Upload intent lifetime must be between 1 and 60 minutes.")
            .ValidateOnStart();
        services.AddSingleton<UploadDocumentValidator>();
        if (r2Enabled)
            services.AddScoped<IUploadProvider, R2UploadProvider>();
        else
            services.AddSingleton<IUploadProvider, LocalUploadProvider>();
        services.AddOptions<DocumentExtractionQualityOptions>()
            .Bind(configuration.GetSection("Documents:Extraction:Quality"));
        services.AddSingleton<PdfDocxDocumentExtractor>();
        services.AddSingleton<IDocumentExtractor>(provider => provider.GetRequiredService<PdfDocxDocumentExtractor>());
        services.AddSingleton<IDetailedDocumentExtractor>(provider => provider.GetRequiredService<PdfDocxDocumentExtractor>());
        var aiEnabled = configuration.GetValue("Features:Ai", true);
        var aiProvider = configuration.GetValue("Ai:Provider", "gemini")?.Trim().ToLowerInvariant() ?? "gemini";
        if (aiProvider is not ("gemini" or "deepseek"))
            throw new InvalidOperationException("Ai:Provider must be gemini or deepseek.");
        var deepSeekEnabled = aiEnabled && string.Equals(aiProvider, "deepseek", StringComparison.Ordinal);
        services.AddOptions<GeminiOptions>().Bind(configuration.GetSection(GeminiOptions.SectionName))
            .Validate(options => !aiEnabled || !string.IsNullOrWhiteSpace(options.ApiKey),
                "Ai:Gemini:ApiKey must be supplied through secret configuration when Features:Ai is enabled.")
            .Validate(options => !aiEnabled || !string.IsNullOrWhiteSpace(options.Model),
                "Ai:Gemini:Model must be configured when Features:Ai is enabled.")
            .Validate(options => string.IsNullOrWhiteSpace(options.Model) || options.Model.Trim().Length <= 80,
                "Ai:Gemini:Model must be 80 characters or fewer.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60, "Gemini timeout must be between 1 and 60 seconds.")
            .Validate(options => options.MaxAttempts == 1,
                "Gemini MaxAttempts must be exactly 1; StructuredAiExecutor owns retries.")
            .Validate(options => options.RetryBaseDelayMilliseconds is >= 0 and <= 5_000, "Gemini retry delay must be between 0 and 5000 milliseconds.")
            .ValidateOnStart();
        services.AddHttpClient<GeminiAiProvider>();
        services.AddOptions<DeepSeekOptions>().Bind(configuration.GetSection(DeepSeekOptions.SectionName))
            .Validate(options => !deepSeekEnabled || !string.IsNullOrWhiteSpace(options.ApiKey),
                "Ai:DeepSeek:ApiKey must be supplied through secret configuration when Ai:Provider=deepseek.")
            .Validate(options => !deepSeekEnabled || DeepSeekConfigurationValidation.IsOfficialBaseUrl(options.BaseUrl),
                "Ai:DeepSeek:BaseUrl must be the official HTTPS DeepSeek API base endpoint.")
            .Validate(options => !deepSeekEnabled || (!string.IsNullOrWhiteSpace(options.Model) && options.Model.Trim().Length <= 80),
                "Ai:DeepSeek:Model must be configured and 80 characters or fewer.")
            .Validate(options => !deepSeekEnabled || options.TimeoutSeconds is >= 1 and <= 120,
                "DeepSeek timeout must be between 1 and 120 seconds.")
            .Validate(options => !deepSeekEnabled || options.MaxAttempts == 1,
                "DeepSeek MaxAttempts must be exactly 1; StructuredAiExecutor owns retries.")
            .Validate(options => !deepSeekEnabled || options.RetryBaseDelayMilliseconds is >= 0 and <= 5_000,
                "DeepSeek retry delay must be between 0 and 5000 milliseconds.")
            .Validate(options => !deepSeekEnabled || DeepSeekConfigurationValidation.IsValidReasoningConfiguration(options.Reasoning),
                "Ai:DeepSeek:Reasoning contains an unsupported policy. Use disabled, or enabled with low, high or max.")
            .ValidateOnStart();
        services.AddHttpClient<DeepSeekAiProvider>();
        services.AddSingleton<IAiProvider>(provider =>
            string.Equals(aiProvider, "deepseek", StringComparison.Ordinal)
                ? provider.GetRequiredService<DeepSeekAiProvider>()
                : provider.GetRequiredService<GeminiAiProvider>());
        services.AddHttpClient<GeminiDocumentOcrProvider>();
        services.AddSingleton<IDocumentOcrProvider>(provider => provider.GetRequiredService<GeminiDocumentOcrProvider>());
        var paymentProvider = configuration.GetValue($"{PaymentProviderOptions.SectionName}:Provider", "fake")?.Trim().ToLowerInvariant() ?? "fake";
        var payosEnabled = string.Equals(paymentProvider, "payos", StringComparison.Ordinal);
        services.AddOptions<PaymentProviderOptions>().Bind(configuration.GetSection(PaymentProviderOptions.SectionName))
            .Validate(options => string.Equals(options.Provider, "fake", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.Provider, "sepay", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.Provider, "payos", StringComparison.OrdinalIgnoreCase), "Billing:Payment:Provider must be fake, sepay or payos.")
            .ValidateOnStart();
        services.AddOptions<FakePaymentOptions>().Bind(configuration.GetSection(FakePaymentOptions.SectionName))
            .Validate(options => options.TimestampToleranceMinutes is > 0 and <= 60, "Fake payment timestamp tolerance must be between 1 and 60 minutes.");
        services.AddOptions<SepayOptions>().Bind(configuration.GetSection(SepayOptions.SectionName))
            .Validate(options => !string.Equals(paymentProvider, "sepay", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.Environment, "Sandbox", StringComparison.Ordinal),
                "Billing:Sepay:Environment=Sandbox is required before production payment approval.")
            .Validate(options => !string.Equals(paymentProvider, "sepay", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(options.MerchantId) && !string.IsNullOrWhiteSpace(options.SecretKey)),
                "Billing:Sepay:MerchantId and Billing:Sepay:SecretKey are required when Billing:Payment:Provider=sepay.")
            .Validate(options => !string.Equals(paymentProvider, "sepay", StringComparison.OrdinalIgnoreCase) ||
                IsExpectedSepayCheckoutUrl(options.CheckoutUrl),
                "Billing:Sepay:CheckoutUrl must be the SePay Sandbox checkout endpoint.")
            .Validate(options => !string.Equals(paymentProvider, "sepay", StringComparison.OrdinalIgnoreCase) ||
                IsExpectedSepayApiUrl(options.ApiBaseUrl),
                "Billing:Sepay:ApiBaseUrl must be the SePay Sandbox API host.")
            .Validate(options => string.IsNullOrWhiteSpace(options.PaymentMethod) ||
                (string.Equals(options.PaymentMethod, options.PaymentMethod.Trim(), StringComparison.Ordinal) &&
                 AllowedSepayPaymentMethods.Contains(options.PaymentMethod, StringComparer.OrdinalIgnoreCase)),
                "Billing:Sepay:PaymentMethod must be CARD, BANK_TRANSFER or NAPAS_BANK_TRANSFER.")
            .Validate(AreValidSepayCallbackUrls,
                "Billing:Sepay callback URLs must be all empty or a same-origin public HTTPS triplet without whitespace, userinfo or fragments (max 2048 characters each).")
            .Validate(options => options.TimeoutSeconds is >= 5 and <= 60, "Billing:Sepay:TimeoutSeconds must be between 5 and 60 seconds.")
            .ValidateOnStart();
        services.AddHttpClient<SepayPaymentProvider>((provider, client) =>
        {
            var sepay = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<SepayOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(sepay.TimeoutSeconds);
        });
        services.AddOptions<PayosOptions>().Bind(configuration.GetSection(PayosOptions.SectionName))
            .Validate(options => !payosEnabled || !string.IsNullOrWhiteSpace(options.ClientId),
                "Billing:Payos:ClientId is required when Billing:Payment:Provider=payos.")
            .Validate(options => !payosEnabled || !string.IsNullOrWhiteSpace(options.ApiKey),
                "Billing:Payos:ApiKey is required when Billing:Payment:Provider=payos.")
            .Validate(options => !payosEnabled || !string.IsNullOrWhiteSpace(options.ChecksumKey),
                "Billing:Payos:ChecksumKey is required when Billing:Payment:Provider=payos.")
            .Validate(options => !payosEnabled || PayosConfigurationValidation.IsValidCallbackUrl(options.ReturnUrl),
                "Billing:Payos:ReturnUrl must be an absolute HTTPS URL, or an HTTP loopback URL for local development, without credentials or a fragment.")
            .Validate(options => !payosEnabled || PayosConfigurationValidation.IsValidCallbackUrl(options.CancelUrl),
                "Billing:Payos:CancelUrl must be an absolute HTTPS URL, or an HTTP loopback URL for local development, without credentials or a fragment.")
            .Validate(options => !payosEnabled || options.TimeoutSeconds is >= 5 and <= 60,
                "Billing:Payos:TimeoutSeconds must be between 5 and 60 seconds.")
            .ValidateOnStart();
        services.AddHttpClient<PayosPaymentProvider>((provider, client) =>
        {
            var payos = provider.GetRequiredService<IOptions<PayosOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(payos.TimeoutSeconds);
        });
        if (payosEnabled)
            services.AddSingleton<IPaymentProvider>(provider => provider.GetRequiredService<PayosPaymentProvider>());
        else if (string.Equals(paymentProvider, "sepay", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IPaymentProvider>(provider => provider.GetRequiredService<SepayPaymentProvider>());
        else
            services.AddSingleton<IPaymentProvider, FakePaymentProvider>();
        return services;
    }

    private static bool IsExpectedSepayCheckoutUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "pay-sandbox.sepay.vn", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath, "/v1/checkout/init", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(uri.Query);

    private static bool IsExpectedSepayApiUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "pgapi-sandbox.sepay.vn", StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath is "/" or "") && string.IsNullOrEmpty(uri.Query);

    private static bool AreValidSepayCallbackUrls(SepayOptions options)
    {
        var values = new[] { options.SuccessUrl, options.ErrorUrl, options.CancelUrl };
        if (values.All(string.IsNullOrEmpty)) return true;
        if (values.Any(string.IsNullOrEmpty)) return false;

        var uris = new Uri[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if (!TryCreatePublicHttpsCallback(values[index], out var uri)) return false;
            uris[index] = uri;
        }

        return IsSameOrigin(uris[0], uris[1]) && IsSameOrigin(uris[0], uris[2]);
    }

    private static bool TryCreatePublicHttpsCallback(string value, out Uri uri)
    {
        uri = null!;
        if (value.Length > MaximumSepayCallbackUrlLength ||
            string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed is null)
            return false;

        uri = parsed;
        return uri.Scheme == Uri.UriSchemeHttps &&
            !uri.IsLoopback &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool IsSameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;
}
