using Microsoft.Extensions.Configuration;
using Nexora.Integrations.Email;
using Nexora.Integrations.Storage;

namespace Nexora.Integrations;

public static class ProductionSafety
{
    public const int MinimumJwtSigningKeyLength = 32;
    private const string DevelopmentJwtSigningKeyPlaceholder = "replace-with-at-least-32-random-characters";

    public static void ValidateDevelopmentAdapters(
        bool isProduction,
        bool aiEnabled,
        bool paymentEnabled,
        bool uploadEnabled,
        string? storageProvider = null)
    {
        var normalizedStorageProvider = storageProvider?.Trim().ToLowerInvariant() ?? "local";
        if (normalizedStorageProvider is not ("local" or "r2"))
            throw new InvalidOperationException("Storage:Provider must be local or r2.");

        if (!isProduction) return;

        var enabled = new List<string>();
        if (aiEnabled) enabled.Add("AI (DEC-01)");
        if (paymentEnabled) enabled.Add("non-production payment adapter (DEC-02)");
        if (normalizedStorageProvider != "r2")
            enabled.Add("filesystem storage (A13)");

        if (enabled.Count == 0) return;
        throw new InvalidOperationException(
            $"Production cannot enable {string.Join(", ", enabled)} before the corresponding production decisions are resolved. Disable the affected Features settings.");
    }

    public static void ValidateDeploymentConfiguration(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var isStaging = string.Equals(environmentName, "Staging", StringComparison.OrdinalIgnoreCase);
        var isProduction = string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        if (!isStaging && !isProduction) return;

        ValidatePostgresConfiguration(configuration);
        ValidateJwtConfiguration(configuration);
        ValidateStorageConfiguration(configuration);
        ValidateEmailConfiguration(isProductionOrStaging: true, configuration);
        ValidateSentryConfiguration(configuration);
        _ = ValidateFrontendOrigins(environmentName, configuration);
    }

    public static void ValidatePostgresConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Postgres")))
            throw new InvalidOperationException("ConnectionStrings:Postgres must be supplied through secret configuration in Staging and Production.");
    }

    public static void ValidateJwtConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var signingKey = configuration["Authentication:Jwt:SigningKey"]?.Trim();
        if (!IsValidJwtSigningKey(signingKey))
            throw new InvalidOperationException(
                "Authentication:Jwt:SigningKey must contain at least 32 non-default characters in Staging and Production.");
    }

    public static bool IsValidJwtSigningKey(string? signingKey)
    {
        if (string.IsNullOrWhiteSpace(signingKey)) return false;
        var normalized = signingKey.Trim();
        return normalized.Length >= MinimumJwtSigningKeyLength &&
            !normalized.All(static character => character == '0') &&
            !string.Equals(normalized, "nexora-local-development-only-signing-key-change-me", StringComparison.Ordinal) &&
            !string.Equals(normalized, DevelopmentJwtSigningKeyPlaceholder, StringComparison.Ordinal);
    }

    public static void ValidateStorageConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var provider = configuration["Storage:Provider"]?.Trim().ToLowerInvariant();
        if (!string.Equals(provider, "r2", StringComparison.Ordinal))
            throw new InvalidOperationException("Staging and Production require Storage:Provider=r2; filesystem storage is not permitted.");

        var options = configuration.GetSection(R2StorageOptions.SectionName).Get<R2StorageOptions>();
        if (!R2ConfigurationValidation.IsValid(options))
            throw new InvalidOperationException("Storage:R2 configuration is incomplete or invalid in Staging and Production.");
    }

    public static void ValidateSentryConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration["Sentry:Dsn"]))
            throw new InvalidOperationException("Sentry:Dsn must be supplied through secret configuration in Staging and Production.");

        if (ResolveSentryRelease(configuration["Sentry:Release"], configuration["RENDER_GIT_COMMIT"]) is null)
            throw new InvalidOperationException(
                "Sentry:Release must be supplied, or RENDER_GIT_COMMIT must be available, in Staging and Production.");
    }

    public static string? ResolveSentryRelease(string? explicitRelease, string? renderCommit)
    {
        var release = string.IsNullOrWhiteSpace(explicitRelease) ? renderCommit : explicitRelease;
        if (string.IsNullOrWhiteSpace(release)) return null;
        var normalized = release.Trim();
        return normalized.Length <= 200 ? normalized : normalized[..200];
    }

    public static string[] ValidateFrontendOrigins(string environmentName, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var configured = configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? [];
        var deployed = string.Equals(environmentName, "Staging", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(environmentName, "Production", StringComparison.OrdinalIgnoreCase);
        if (!deployed)
            return configured.Where(static origin => !string.IsNullOrWhiteSpace(origin))
                .Select(static origin => origin.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        if (configured.Length == 0)
            throw new InvalidOperationException("Frontend:AllowedOrigins must contain at least one HTTPS frontend origin in Staging and Production.");

        var normalized = new List<string>(configured.Length);
        foreach (var origin in configured)
        {
            if (!TryNormalizeFrontendOrigin(origin, requirePublicHttps: true, out var normalizedOrigin))
                throw new InvalidOperationException("Frontend:AllowedOrigins contains an invalid production frontend origin.");
            if (!normalized.Contains(normalizedOrigin, StringComparer.OrdinalIgnoreCase))
                normalized.Add(normalizedOrigin);
        }

        if (normalized.Count == 0)
            throw new InvalidOperationException("Frontend:AllowedOrigins must contain at least one HTTPS frontend origin in Staging and Production.");
        return normalized.ToArray();
    }

    public static bool TryNormalizeFrontendOrigin(string? origin, out string normalizedOrigin) =>
        TryNormalizeFrontendOrigin(origin, requirePublicHttps: false, out normalizedOrigin);

    public static bool IsAllowedFrontendOrigin(string? origin, IEnumerable<string> allowedOrigins)
    {
        ArgumentNullException.ThrowIfNull(allowedOrigins);
        if (string.IsNullOrWhiteSpace(origin)) return false;
        if (allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;
        if (!TryNormalizeFrontendOrigin(origin, out var normalizedOrigin)) return false;

        return allowedOrigins.Any(allowedOrigin =>
            TryNormalizeFrontendOrigin(allowedOrigin, out var normalizedAllowedOrigin) &&
            string.Equals(normalizedOrigin, normalizedAllowedOrigin, StringComparison.OrdinalIgnoreCase));
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
        if (!EmailConfigurationRules.IsValidApiKey(apiKey) || IsTemplateValue(apiKey))
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

    private static bool TryNormalizeFrontendOrigin(string? origin, bool requirePublicHttps, out string normalizedOrigin)
    {
        normalizedOrigin = string.Empty;
        if (string.IsNullOrWhiteSpace(origin) || origin == "*" ||
            origin.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character)) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            uri is null ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("/" or "") ||
            (requirePublicHttps && (uri.Scheme != Uri.UriSchemeHttps || uri.IsLoopback)))
            return false;

        var builder = new UriBuilder(uri.Scheme.ToLowerInvariant(), uri.IdnHost.ToLowerInvariant())
        {
            Port = uri.IsDefaultPort ? -1 : uri.Port
        };
        normalizedOrigin = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        return true;
    }

    private static bool IsTemplateValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Contains("replace-with", StringComparison.OrdinalIgnoreCase);
}
