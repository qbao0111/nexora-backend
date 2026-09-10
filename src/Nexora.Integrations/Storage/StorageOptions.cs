namespace Nexora.Integrations.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string Provider { get; set; } = "local";
}

public sealed class R2StorageOptions
{
    public const string SectionName = "Storage:R2";

    public string AccountId { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
}

public static class R2ConfigurationValidation
{
    public static bool IsValidEndpoint(string? endpoint)
    {
        if (!Uri.TryCreate(endpoint?.Trim(), UriKind.Absolute, out var uri) || uri is null)
            return false;

        return uri.Scheme == Uri.UriSchemeHttps &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment);
    }
}
