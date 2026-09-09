using System.Net.Mail;

namespace Nexora.Integrations.Email;

public sealed class EmailOptions
{
    public const string SectionName = "Email";
    public string Provider { get; set; } = "noop";
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Nexora";
}

public sealed class ResendEmailOptions
{
    public const string SectionName = "Email:Resend";
    public string ApiKey { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = "https://api.resend.com";
    public int TimeoutSeconds { get; set; } = 15;
}

internal static class EmailConfigurationRules
{
    public const string NoopProvider = "noop";
    public const string ResendProvider = "resend";

    public static bool IsSupportedProvider(string? provider) =>
        string.Equals(provider, NoopProvider, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(provider, ResendProvider, StringComparison.OrdinalIgnoreCase);

    public static bool IsValidFromAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || ContainsControlCharacter(address) || address.Length > 254)
            return false;

        try
        {
            var parsed = new MailAddress(address.Trim());
            return string.Equals(parsed.Address, address.Trim(), StringComparison.Ordinal) &&
                parsed.Host.Contains('.', StringComparison.Ordinal) &&
                !parsed.Host.StartsWith('.') &&
                !parsed.Host.EndsWith('.');
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static bool IsValidDisplayName(string? displayName) =>
        !string.IsNullOrWhiteSpace(displayName) &&
        displayName.Trim().Length <= 100 &&
        !ContainsControlCharacter(displayName);

    public static bool IsOfficialResendBaseUrl(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            uri.Scheme == Uri.UriSchemeHttps &&
            string.Equals(uri.Host, "api.resend.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.AbsolutePath is "/" or "") &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            string.IsNullOrEmpty(uri.UserInfo);
    }

    public static bool IsValidApiKey(string? apiKey) =>
        !string.IsNullOrWhiteSpace(apiKey) &&
        apiKey.Trim().Length <= 256 &&
        !ContainsControlCharacter(apiKey);

    public static bool IsSafeLink(Uri? link)
    {
        return link is { IsAbsoluteUri: true } &&
            (link.Scheme == Uri.UriSchemeHttps || link.Scheme == Uri.UriSchemeHttp) &&
            string.IsNullOrEmpty(link.UserInfo) &&
            string.IsNullOrEmpty(link.Fragment);
    }

    private static bool ContainsControlCharacter(string value) => value.Any(char.IsControl);
}
