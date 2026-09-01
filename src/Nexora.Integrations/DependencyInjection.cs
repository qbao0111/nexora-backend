using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Integrations.Ai;
using Nexora.Integrations.Payments;
using Nexora.Integrations.Storage;

namespace Nexora.Integrations;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LocalStorageOptions>().Bind(configuration.GetSection(LocalStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "Storage:Local:RootPath is required.")
            .ValidateOnStart();
        services.AddSingleton<IStorageProvider, LocalStorageProvider>();
        services.AddOptions<UploadOptions>().Bind(configuration.GetSection(UploadOptions.SectionName))
            .Validate(options => options.MaxResumeBytes is > 0 and <= 25 * 1024 * 1024, "Upload limit must be between 1 byte and 25 MiB.")
            .Validate(options => options.IntentMinutes is > 0 and <= 60, "Upload intent lifetime must be between 1 and 60 minutes.");
        services.AddSingleton<IUploadProvider, LocalUploadProvider>();
        services.AddOptions<DocumentExtractionQualityOptions>()
            .Bind(configuration.GetSection("Documents:Extraction:Quality"));
        services.AddSingleton<PdfDocxDocumentExtractor>();
        services.AddSingleton<IDocumentExtractor>(provider => provider.GetRequiredService<PdfDocxDocumentExtractor>());
        services.AddSingleton<IDetailedDocumentExtractor>(provider => provider.GetRequiredService<PdfDocxDocumentExtractor>());
        var aiEnabled = configuration.GetValue("Features:Ai", true);
        services.AddOptions<GeminiOptions>().Bind(configuration.GetSection(GeminiOptions.SectionName))
            .Validate(options => !aiEnabled || !string.IsNullOrWhiteSpace(options.ApiKey),
                "Ai:Gemini:ApiKey must be supplied through secret configuration when Features:Ai is enabled.")
            .Validate(options => !aiEnabled || !string.IsNullOrWhiteSpace(options.Model),
                "Ai:Gemini:Model must be configured when Features:Ai is enabled.")
            .Validate(options => string.IsNullOrWhiteSpace(options.Model) || options.Model.Trim().Length <= 80,
                "Ai:Gemini:Model must be 80 characters or fewer.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60, "Gemini timeout must be between 1 and 60 seconds.")
            .Validate(options => options.MaxAttempts is >= 1 and <= 3, "Gemini attempts must be between 1 and 3.")
            .Validate(options => options.RetryBaseDelayMilliseconds is >= 0 and <= 5_000, "Gemini retry delay must be between 0 and 5000 milliseconds.")
            .ValidateOnStart();
        services.AddHttpClient<GeminiAiProvider>();
        services.AddSingleton<IAiProvider>(provider => provider.GetRequiredService<GeminiAiProvider>());
        services.AddHttpClient<GeminiDocumentOcrProvider>();
        services.AddSingleton<IDocumentOcrProvider>(provider => provider.GetRequiredService<GeminiDocumentOcrProvider>());
        var paymentProvider = configuration.GetValue($"{PaymentProviderOptions.SectionName}:Provider", "fake")?.Trim().ToLowerInvariant() ?? "fake";
        services.AddOptions<PaymentProviderOptions>().Bind(configuration.GetSection(PaymentProviderOptions.SectionName))
            .Validate(options => string.Equals(options.Provider, "fake", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.Provider, "vnpay", StringComparison.OrdinalIgnoreCase), "Billing:Payment:Provider must be fake or vnpay.")
            .ValidateOnStart();
        services.AddOptions<FakePaymentOptions>().Bind(configuration.GetSection(FakePaymentOptions.SectionName))
            .Validate(options => options.TimestampToleranceMinutes is > 0 and <= 60, "Fake payment timestamp tolerance must be between 1 and 60 minutes.");
        services.AddOptions<VnpayOptions>().Bind(configuration.GetSection(VnpayOptions.SectionName))
            .Validate(options => !string.Equals(paymentProvider, "vnpay", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.Environment, "Sandbox", StringComparison.OrdinalIgnoreCase),
                "Only Billing:Vnpay:Environment=Sandbox is supported before DEC-02 production payment approval.")
            .Validate(options => !string.Equals(paymentProvider, "vnpay", StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(options.TmnCode) &&
                 !string.IsNullOrWhiteSpace(options.HashSecret) &&
                 Uri.TryCreate(options.ReturnUrl, UriKind.Absolute, out _)),
                "VNPAY sandbox configuration is required when Billing:Payment:Provider=vnpay.")
            .Validate(options => !string.Equals(paymentProvider, "vnpay", StringComparison.OrdinalIgnoreCase) ||
                IsExpectedVnpayUrl(options.PaymentUrl, "/paymentv2/vpcpay.html"),
                "Billing:Vnpay:PaymentUrl must be the VNPAY sandbox payment endpoint.")
            .Validate(options => !string.Equals(paymentProvider, "vnpay", StringComparison.OrdinalIgnoreCase) ||
                IsExpectedVnpayUrl(options.QueryUrl, "/merchant_webapi/api/transaction"),
                "Billing:Vnpay:QueryUrl must be the VNPAY sandbox query endpoint.")
            .Validate(options => string.Equals(options.Locale, "vn", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(options.Locale, "en", StringComparison.OrdinalIgnoreCase), "Billing:Vnpay:Locale must be vn or en.")
            .Validate(options => options.ExpireMinutes is >= 5 and <= 60, "Billing:Vnpay:ExpireMinutes must be between 5 and 60 minutes.")
            .Validate(options => options.TimeoutSeconds is >= 30 and <= 60, "Billing:Vnpay:TimeoutSeconds must be between 30 and 60 seconds.")
            .ValidateOnStart();
        services.AddHttpClient<VnpayPaymentProvider>((provider, client) =>
        {
            var vnpay = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<VnpayOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(vnpay.TimeoutSeconds);
        });
        if (string.Equals(paymentProvider, "vnpay", StringComparison.OrdinalIgnoreCase))
            services.AddSingleton<IPaymentProvider>(provider => provider.GetRequiredService<VnpayPaymentProvider>());
        else
            services.AddSingleton<IPaymentProvider, FakePaymentProvider>();
        return services;
    }

    private static bool IsExpectedVnpayUrl(string value, string path) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.Equals(uri.Host, "sandbox.vnpayment.vn", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(uri.AbsolutePath, path, StringComparison.Ordinal);
}
