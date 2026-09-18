namespace Nexora.Integrations.Payments;

public sealed class PayosOptions
{
    public const string SectionName = "Billing:Payos";

    public string ClientId { get; init; } = string.Empty;
    public string ApiKey { get; init; } = string.Empty;
    public string ChecksumKey { get; init; } = string.Empty;
    public string ReturnUrl { get; init; } = string.Empty;
    public string CancelUrl { get; init; } = string.Empty;
    public int TimeoutSeconds { get; init; } = 15;
}

public static class PayosConfigurationValidation
{
    public const int MaximumCallbackUrlLength = 2048;

    public static bool IsValidCallbackUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumCallbackUrlLength ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri is null ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment))
            return false;

        return uri.Scheme == Uri.UriSchemeHttps ||
            (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
    }
}
