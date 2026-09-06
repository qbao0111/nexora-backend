namespace Nexora.Business.Auth;

public sealed record RegisterUserCommand(string Email, string Password, string? DisplayName);
public sealed record LoginUserCommand(string Email, string Password);
public sealed record ExternalIdentity(string Provider, string ProviderSubject, string Email, string? DisplayName);
public sealed record AuthenticatedUser(Guid Id, string Email, string? DisplayName, IReadOnlyCollection<string> Roles);
public sealed record AuthSession(AuthenticatedUser User, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public interface IAuthService
{
    Task<AuthSession> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken);
    Task<AuthSession> LoginAsync(LoginUserCommand command, CancellationToken cancellationToken);
    Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<AuthenticatedUser> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<AuthenticatedUser> UpdateProfileAsync(Guid userId, string? displayName, CancellationToken cancellationToken);
    Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken);
}

public interface IExternalIdentityProvider
{
    string ProviderName { get; }
    Task<ExternalIdentity> ValidateCallbackAsync(string authorizationCode, Uri redirectUri, CancellationToken cancellationToken);
}
