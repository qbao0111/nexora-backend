namespace Nexora.Business.Auth;

public sealed record RegisterUserCommand(string Email, string Password, string? DisplayName);
public sealed record RegistrationResult(string Email, bool VerificationRequired);
public sealed record LoginUserCommand(string Email, string Password);
public sealed record ForgotPasswordCommand(string Email);
public sealed record ResetPasswordCommand(Guid UserId, string Token, string NewPassword);
public sealed record VerifyEmailCommand(Guid UserId, string Token);
public sealed record ResendVerificationCommand(string Email);
public sealed record EmailVerificationResult(string Email, bool AlreadyVerified);
public sealed record ExternalIdentity(string Provider, string ProviderSubject, string Email, string? DisplayName);
public sealed record AuthenticatedUser(
    Guid Id,
    string Email,
    string? DisplayName,
    IReadOnlyCollection<string> Roles,
    int? YearsOfExperience = null);
public sealed record AuthSession(AuthenticatedUser User, string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

public interface IAuthService
{
    Task<RegistrationResult> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken);
    Task<EmailVerificationResult> VerifyEmailAsync(VerifyEmailCommand command, CancellationToken cancellationToken);
    Task ResendVerificationAsync(ResendVerificationCommand command, CancellationToken cancellationToken);
    Task ForgotPasswordAsync(ForgotPasswordCommand command, CancellationToken cancellationToken);
    Task ResetPasswordAsync(ResetPasswordCommand command, CancellationToken cancellationToken);
    Task<AuthSession> LoginAsync(LoginUserCommand command, CancellationToken cancellationToken);
    Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
    Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken);
    Task<AuthenticatedUser> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken);
    Task<AuthenticatedUser> UpdateProfileAsync(
        Guid userId,
        string? displayName,
        int? yearsOfExperience,
        CancellationToken cancellationToken);
    Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken);
}

public interface IExternalIdentityProvider
{
    string ProviderName { get; }
    Task<ExternalIdentity> ValidateCallbackAsync(string authorizationCode, Uri redirectUri, CancellationToken cancellationToken);
}
