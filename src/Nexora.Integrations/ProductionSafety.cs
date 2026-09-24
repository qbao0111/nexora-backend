using Microsoft.Extensions.Configuration;
using Nexora.Integrations.Email;

namespace Nexora.Integrations;

public static class ProductionSafety
{
    public static void ValidateDevelopmentAdapters(
        bool isProduction,
        bool aiEnabled,
        bool paymentEnabled,
        bool uploadEnabled,
        string? storageProvider = null,
        string? paymentProvider = null,
        bool isStaging = false,
        bool localPersistentVolumeConfigured = false)
    {
        var normalizedStorageProvider = storageProvider?.Trim().ToLowerInvariant() ?? "local";
        var normalizedPaymentProvider = paymentProvider?.Trim().ToLowerInvariant() ?? "fake";
        if (normalizedStorageProvider is not ("local" or "r2"))
            throw new InvalidOperationException("Storage:Provider must be local or r2.");

        if ((isProduction || isStaging) && normalizedStorageProvider == "local" && !localPersistentVolumeConfigured)
            throw new InvalidOperationException(
                "Staging and Production require durable storage. Set Storage:Provider=r2, or explicitly configure Storage:Local:PersistentVolumeConfigured=true only for a mounted persistent volume.");

        if (!isProduction) return;
        var enabled = new List<string>();
        if (aiEnabled) enabled.Add("AI (DEC-01)");
        if (paymentEnabled) enabled.Add($"{PaymentAdapterName(normalizedPaymentProvider)} (DEC-02)");
        if (uploadEnabled && normalizedStorageProvider != "r2")
            enabled.Add("development upload adapter (A2/DEC-04)");
        if (enabled.Count == 0) return;
        throw new InvalidOperationException(
            $"Production cannot enable {string.Join(", ", enabled)} before the corresponding production decisions are resolved. Disable the affected Features settings.");
    }

    private static string PaymentAdapterName(string provider) => provider switch
    {
        "fake" => "Fake payment adapter",
        "sepay" => "SePay payment adapter",
        "payos" => "payOS payment adapter",
        _ => "payment adapter"
    };

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
