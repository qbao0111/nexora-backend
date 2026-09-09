namespace Nexora.Business.Auth;

public sealed class EmailVerificationOptions
{
    public const string SectionName = "Authentication:EmailVerification";

    public string PublicUrl { get; set; } = string.Empty;
    public int TokenLifespanHours { get; set; } = 24;
}
