using Microsoft.Extensions.Configuration;
using Nexora.Integrations.Email;

namespace Nexora.Integrations;

public sealed record ProductionAdapterSelection(
    bool AiEnabled,
    bool PaymentEnabled,
    string? AiProvider,
    string? PaymentProvider,
    string? StorageProvider,
    bool LocalPersistentVolumeConfigured = false)
{
    public static ProductionAdapterSelection FromConfiguration(IConfiguration configuration) => new(
        configuration.GetValue("Features:Ai", true),
        configuration.GetValue("Features:Payment", true),
        configuration.GetValue<string?>("Ai:Provider")?.Trim().ToLowerInvariant() ?? "gemini",
        configuration.GetValue<string?>("Billing:Payment:Provider")?.Trim().ToLowerInvariant() ?? "fake",
        configuration.GetValue<string?>("Storage:Provider")?.Trim().ToLowerInvariant() ?? "local",
        configuration.GetValue("Storage:Local:PersistentVolumeConfigured", false));
}

public static class ProductionSafety
{
    public static void ValidateAdapters(
        bool isProduction,
        bool isStaging,
        ProductionAdapterSelection adapters)
    {
        var normalizedStorageProvider = adapters.StorageProvider?.Trim().ToLowerInvariant() ?? "local";
        if (normalizedStorageProvider is not ("local" or "r2"))
            throw new InvalidOperationException("Storage:Provider must be local or r2.");

        if (isProduction && normalizedStorageProvider != "r2")
            throw new InvalidOperationException("Production requires durable storage with Storage:Provider=r2, even when uploads are disabled.");

        if (isStaging && normalizedStorageProvider == "local" && !adapters.LocalPersistentVolumeConfigured)
            throw new InvalidOperationException(
                "Staging requires durable storage. Set Storage:Provider=r2, or explicitly configure Storage:Local:PersistentVolumeConfigured=true only for a mounted persistent volume.");

        if (!isProduction) return;
        if (adapters.AiEnabled && !string.Equals(adapters.AiProvider?.Trim(), "deepseek", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production AI requires Ai:Provider=deepseek when Features:Ai is enabled.");
        if (adapters.PaymentEnabled && !string.Equals(adapters.PaymentProvider?.Trim(), "payos", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Production payment requires Billing:Payment:Provider=payos when Features:Payment is enabled.");
    }

    public static void ValidateEmailConfiguration(bool isProductionOrStaging, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!isProductionOrStaging) return;

        var provider = configuration.GetValue<string?>($"{EmailOptions.SectionName}:Provider")?.Trim();
        if (string.IsNullOrEmpty(provider) || string.Equals(provider, EmailConfigurationRules.NoopProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Production and Staging environments require a real email provider (resend). The 'noop' provider is not permitted outside Development and Testing.");
        }

        if (!string.Equals(provider, EmailConfigurationRules.ResendProvider, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Email:Provider must be 'resend' in Production and Staging environments, but was '{provider}'.");
        }

        var fromAddress = configuration.GetValue<string?>($"{EmailOptions.SectionName}:FromAddress");
        if (!EmailConfigurationRules.IsValidFromAddress(fromAddress))
        {
            throw new InvalidOperationException(
                "Email:FromAddress must be a valid domain email address in Production and Staging.");
        }

        var fromName = configuration.GetValue<string?>($"{EmailOptions.SectionName}:FromName");
        if (!EmailConfigurationRules.IsValidDisplayName(fromName))
        {
            throw new InvalidOperationException(
                "Email:FromName is required and must not contain control characters in Production and Staging.");
        }

        var apiKey = configuration.GetValue<string?>($"{ResendEmailOptions.SectionName}:ApiKey");
        if (!EmailConfigurationRules.IsValidApiKey(apiKey))
        {
            throw new InvalidOperationException(
                "Email:Resend:ApiKey must be supplied through secret configuration in Production and Staging.");
        }

        var apiBaseUrl = configuration.GetValue<string?>($"{ResendEmailOptions.SectionName}:ApiBaseUrl") ?? "https://api.resend.com";
        if (!EmailConfigurationRules.IsOfficialResendBaseUrl(apiBaseUrl))
        {
            throw new InvalidOperationException(
                "Email:Resend:ApiBaseUrl must be the official HTTPS Resend API endpoint in Production and Staging.");
        }

        var publicUrl = configuration.GetValue<string?>("Authentication:EmailVerification:PublicUrl");
        if (string.IsNullOrWhiteSpace(publicUrl) ||
            !Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            uri.IsLoopback)
        {
            throw new InvalidOperationException(
                "Authentication:EmailVerification:PublicUrl must be an absolute HTTPS URL in Production and Staging.");
        }
    }
}
