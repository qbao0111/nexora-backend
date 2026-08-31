using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
        services.AddOptions<GeminiOptions>().Bind(configuration.GetSection(GeminiOptions.SectionName))
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60, "Gemini timeout must be between 1 and 60 seconds.")
            .Validate(options => options.MaxAttempts is >= 1 and <= 3, "Gemini attempts must be between 1 and 3.")
            .Validate(options => options.RetryBaseDelayMilliseconds is >= 0 and <= 5_000, "Gemini retry delay must be between 0 and 5000 milliseconds.");
        services.AddHttpClient<GeminiAiProvider>();
        var aiProvider = configuration["Ai:Provider"] ?? "Fake";
        services.AddSingleton<IAiProvider>(provider =>
        {
            if (!string.Equals(aiProvider, "Gemini", StringComparison.OrdinalIgnoreCase)) return new FakeAiProvider();
            if (provider.GetRequiredService<IHostEnvironment>().IsProduction())
                throw new InvalidOperationException("Gemini development adapter cannot be enabled in Production before DEC-01.");
            return provider.GetRequiredService<GeminiAiProvider>();
        });
        services.AddOptions<FakePaymentOptions>().Bind(configuration.GetSection(FakePaymentOptions.SectionName))
            .Validate(options => options.TimestampToleranceMinutes is > 0 and <= 60, "Fake payment timestamp tolerance must be between 1 and 60 minutes.");
        services.AddSingleton<IPaymentProvider, FakePaymentProvider>();
        return services;
    }
}
