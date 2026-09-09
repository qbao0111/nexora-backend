namespace Nexora.Business.Storage;

/// <summary>
/// Validates logical object keys before a provider resolves or sends them.
/// </summary>
public static class StorageKeyValidator
{
    public static string Validate(string storageKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);

        if (!string.Equals(storageKey, storageKey.Trim(), StringComparison.Ordinal) ||
            storageKey.StartsWith('/') ||
            storageKey.Contains('\\') ||
            (storageKey.Length >= 2 && char.IsLetter(storageKey[0]) && storageKey[1] == ':') ||
            storageKey.Any(char.IsControl))
            throw new ArgumentException("Storage key is invalid.", nameof(storageKey));

        var segments = storageKey.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".."))
            throw new ArgumentException("Storage key is invalid.", nameof(storageKey));

        return storageKey;
    }
}
