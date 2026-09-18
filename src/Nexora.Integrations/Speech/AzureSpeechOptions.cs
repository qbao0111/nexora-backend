namespace Nexora.Integrations.Speech;

public sealed class AzureSpeechOptions
{
    public const string SectionName = "Speech:Azure";

    public string Key { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 10;
    public int ServerRefreshMinutes { get; set; } = 8;
    public int ClientUsableMinutes { get; set; } = 9;

    public static bool IsValidRegion(string? region)
    {
        if (string.IsNullOrWhiteSpace(region) || region.Length is < 2 or > 63 ||
            region[0] == '-' || region[^1] == '-')
            return false;

        var previousWasHyphen = false;
        foreach (var character in region)
        {
            var isAsciiLetterOrDigit = character is >= 'a' and <= 'z' or >= '0' and <= '9';
            if (character == '-')
            {
                if (previousWasHyphen) return false;
                previousWasHyphen = true;
                continue;
            }

            if (!isAsciiLetterOrDigit) return false;
            previousWasHyphen = false;
        }

        return true;
    }
}
