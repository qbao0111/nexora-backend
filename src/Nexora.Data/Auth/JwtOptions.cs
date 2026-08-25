namespace Nexora.Data.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Authentication:Jwt";
    public string Issuer { get; init; } = "Nexora.Api";
    public string Audience { get; init; } = "Nexora.Frontend";
    public string SigningKey { get; init; } = string.Empty;
    public int AccessTokenMinutes { get; init; } = 15;
    public int RefreshTokenDays { get; init; } = 30;
}
