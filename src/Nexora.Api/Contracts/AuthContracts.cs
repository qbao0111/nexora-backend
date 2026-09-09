using System.ComponentModel.DataAnnotations;

namespace Nexora.Api.Contracts;

public sealed class RegisterRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; init; } = string.Empty;
    [Required, MinLength(10), MaxLength(128)] public string Password { get; init; } = string.Empty;
    [MaxLength(120)] public string? DisplayName { get; init; }
}
public sealed class LoginRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; init; } = string.Empty;
    [Required, MaxLength(128)] public string Password { get; init; } = string.Empty;
}
public sealed class VerifyEmailRequest
{
    [Required] public Guid UserId { get; init; }
    [Required, MaxLength(2048)] public string Token { get; init; } = string.Empty;
}
public sealed class ResendVerificationRequest
{
    [Required, EmailAddress, MaxLength(256)] public string Email { get; init; } = string.Empty;
}
public sealed class UpdateProfileRequest { [MaxLength(120)] public string? DisplayName { get; init; } }
public sealed class ChangePasswordRequest
{
    [Required, MaxLength(128)] public string CurrentPassword { get; init; } = string.Empty;
    [Required, MinLength(10), MaxLength(128)] public string NewPassword { get; init; } = string.Empty;
}
public sealed record UserResponse(Guid Id, string Email, string? DisplayName, IReadOnlyCollection<string> Roles, BillingSummaryResponse? Billing = null);
public sealed record AuthSessionResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, UserResponse User);
public sealed record RegistrationResponse(string Email, bool VerificationRequired);
public sealed record EmailVerificationResponse(string Email, bool AlreadyVerified);
public sealed record ResendVerificationResponse(string Message);
