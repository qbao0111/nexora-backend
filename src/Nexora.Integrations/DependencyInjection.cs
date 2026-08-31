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
        services.AddOptions<FakePaymentOptions>().Bind(configuration.GetSection(FakePaymentOptions.SectionName))
            .Validate(options => options.TimestampToleranceMinutes is > 0 and <= 60, "Fake payment timestamp tolerance must be between 1 and 60 minutes.");
        services.AddSingleton<IPaymentProvider, FakePaymentProvider>();
        return services;
    }
}
