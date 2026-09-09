using System.Data;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Nexora.Business.Auth;
using Nexora.Business.Common;
using Nexora.Business.Email;
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.Data.Auth;

public sealed class IdentityAuthService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    NexoraDbContext dbContext,
    IOptions<JwtOptions> jwtOptions,
    IOptions<EmailVerificationOptions> emailVerificationOptions,
    IEmailSender emailSender,
    TimeProvider timeProvider) : IAuthService
{
    public const string SecurityStampClaim = "nexora:security_stamp";
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;
    private readonly EmailVerificationOptions _emailVerificationOptions = emailVerificationOptions.Value;

    public async Task<RegistrationResult> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken)
    {
        var email = NormalizeEmail(command.Email);
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            throw new BusinessException("EMAIL_ALREADY_REGISTERED", "Email này đã được đăng ký.", BusinessErrorKind.Conflict);
        }

        var now = timeProvider.GetUtcNow();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = email,
            Email = email,
            CreatedAt = now,
            UpdatedAt = now,
            IsActive = true,
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var result = await userManager.CreateAsync(user, command.Password);
        if (!result.Succeeded) throw IdentityValidation(result);

        var roleResult = await userManager.AddToRoleAsync(user, Nexora.Business.Authorization.RoleNames.User);
        if (!roleResult.Succeeded) throw IdentityValidation(roleResult);

        dbContext.UserProfiles.Add(new UserProfile
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            DisplayName = NormalizeDisplayName(command.DisplayName),
            CreatedAt = now,
            UpdatedAt = now
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await SendVerificationEmailAsync(user, cancellationToken);
        return new RegistrationResult(user.Email!, VerificationRequired: true);
    }

    public async Task<EmailVerificationResult> VerifyEmailAsync(VerifyEmailCommand command, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(command.UserId.ToString());
        if (user is null || !user.IsActive || user.DeletionRequestedAt is not null || user.DeletedAt is not null)
            throw InvalidEmailVerification();

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var alreadyVerified = user.EmailConfirmed;
        var result = await userManager.ConfirmEmailAsync(user, command.Token);
        if (!result.Succeeded)
            throw InvalidEmailVerification();

        var now = timeProvider.GetUtcNow();
        user.UpdatedAt = now;
        await EnsureFreeEntitlementAsync(user.Id, now, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new EmailVerificationResult(user.Email!, alreadyVerified);
    }

    public async Task ResendVerificationAsync(ResendVerificationCommand command, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(NormalizeEmail(command.Email));
        if (user is null || !user.IsActive || user.EmailConfirmed || user.DeletionRequestedAt is not null || user.DeletedAt is not null)
            return;

        await SendVerificationEmailAsync(user, cancellationToken, invalidatePreviousTokens: true);
    }

    public async Task<AuthSession> LoginAsync(LoginUserCommand command, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(NormalizeEmail(command.Email));
        if (user is null || !user.IsActive || user.DeletionRequestedAt is not null || user.DeletedAt is not null ||
            !(await signInManager.CheckPasswordSignInAsync(user, command.Password, true)).Succeeded)
        {
            throw new BusinessException("INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.", BusinessErrorKind.Unauthorized);
        }
        if (!user.EmailConfirmed)
            throw new BusinessException("EMAIL_NOT_VERIFIED", "Bạn cần xác minh email trước khi đăng nhập.", BusinessErrorKind.Unauthorized);
        return await CreateSessionAsync(user, cancellationToken);
    }

    public async Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = HashToken(refreshToken);
        var existing = await dbContext.RefreshTokens.Include(token => token.User)
            .SingleOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);
        if (existing is null || existing.ExpiresAt <= now || !existing.User.IsActive || !existing.User.EmailConfirmed || existing.User.DeletionRequestedAt is not null || existing.User.DeletedAt is not null)
            throw InvalidRefreshToken();

        var replacement = CreateRefreshToken(existing.UserId, now);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (existing.RevokedAt is not null) throw InvalidRefreshToken();
        existing.RevokedAt = now;
        existing.ReplacedByTokenHash = replacement.Entity.TokenHash;
        existing.ConcurrencyToken = Guid.NewGuid();
        dbContext.RefreshTokens.Add(replacement.Entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw InvalidRefreshToken();
        }
        await transaction.CommitAsync(cancellationToken);
        return await CreateSessionResponseAsync(existing.User, replacement.RawToken, replacement.Entity.ExpiresAt, cancellationToken);
    }

    public Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken) =>
        dbContext.RefreshTokens.Where(token => token.TokenHash == HashToken(refreshToken) && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, timeProvider.GetUtcNow()), cancellationToken);

    public async Task RevokeAllSessionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString()) ?? throw UserNotFound();
        var now = timeProvider.GetUtcNow();
        await dbContext.RefreshTokens.Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);
        var result = await userManager.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) throw new BusinessException("SESSION_REVOCATION_FAILED", "Không thể thu hồi phiên đăng nhập.", BusinessErrorKind.ExternalFailure);
    }

    public async Task<AuthenticatedUser> GetCurrentUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.AsNoTracking().Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Id == userId && item.IsActive && item.DeletionRequestedAt == null && item.DeletedAt == null, cancellationToken) ?? throw UserNotFound();
        return await MapUserAsync(user);
    }

    public async Task<AuthenticatedUser> UpdateProfileAsync(Guid userId, string? displayName, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Id == userId && item.IsActive && item.DeletionRequestedAt == null && item.DeletedAt == null, cancellationToken) ?? throw UserNotFound();
        var now = timeProvider.GetUtcNow();
        user.Profile ??= new UserProfile { Id = Guid.NewGuid(), UserId = user.Id, CreatedAt = now };
        user.Profile.DisplayName = NormalizeDisplayName(displayName);
        user.Profile.UpdatedAt = now;
        user.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await MapUserAsync(user);
    }

    public async Task ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentPassword) || string.IsNullOrWhiteSpace(newPassword))
            throw new BusinessException("INVALID_PASSWORD", "Mật khẩu không được để trống.", BusinessErrorKind.Validation);
        if (newPassword.Length < 10 || newPassword.Length > 128)
            throw new BusinessException("PASSWORD_LENGTH_INVALID", "Mật khẩu mới phải từ 10 đến 128 ký tự.", BusinessErrorKind.Validation);

        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || !user.IsActive || user.DeletionRequestedAt is not null || user.DeletedAt is not null)
            throw UserNotFound();

        var changeResult = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!changeResult.Succeeded)
        {
            var isCurrentPasswordIncorrect = changeResult.Errors.Any(e => e.Code == "PasswordMismatch");
            if (isCurrentPasswordIncorrect)
                throw new BusinessException("INCORRECT_CURRENT_PASSWORD", "Mật khẩu hiện tại không chính xác.", BusinessErrorKind.Unauthorized);
            throw IdentityValidation(changeResult);
        }

        // Revoke all existing sessions and refresh tokens on password change
        var now = timeProvider.GetUtcNow();
        await dbContext.RefreshTokens.Where(token => token.UserId == userId && token.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(token => token.RevokedAt, now), cancellationToken);
        var stampResult = await userManager.UpdateSecurityStampAsync(user);
        if (!stampResult.Succeeded)
            throw new BusinessException("SESSION_REVOCATION_FAILED", "Không thể cập nhật bảo mật sau khi đổi mật khẩu.", BusinessErrorKind.ExternalFailure);
    }

    private async Task SendVerificationEmailAsync(ApplicationUser user, CancellationToken cancellationToken, bool invalidatePreviousTokens = false)
    {
        if (invalidatePreviousTokens)
        {
            var stampResult = await userManager.UpdateSecurityStampAsync(user);
            if (!stampResult.Succeeded)
                throw new BusinessException("EMAIL_VERIFICATION_FAILED", "Không thể tạo lại liên kết xác minh email.", BusinessErrorKind.ExternalFailure);
        }
        var token = await userManager.GenerateEmailConfirmationTokenAsync(user);
        var baseUrl = _emailVerificationOptions.PublicUrl.TrimEnd('/');
        var link = new Uri($"{baseUrl}/verify-email?userId={user.Id:D}&token={Uri.EscapeDataString(token)}", UriKind.Absolute);
        await emailSender.SendVerificationAsync(
            new VerificationEmail(new EmailRecipient(user.Email!), link), cancellationToken);
    }

    private async Task EnsureFreeEntitlementAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var hasFreeEntitlement = await dbContext.Entitlements.AnyAsync(
            item => item.UserId == userId && item.PlanCodeSnapshot == "free" && item.Status == Nexora.Business.Billing.BillingValues.Active,
            cancellationToken);
        if (hasFreeEntitlement) return;

        var freePlanPrice = await dbContext.PlanPrices
            .Include(item => item.Plan)
            .Include(item => item.Features)
            .ThenInclude(item => item.FeatureDefinition)
            .SingleOrDefaultAsync(item => item.Plan.Code == "free" && item.IsActive, cancellationToken);
        if (freePlanPrice is null) return;

        var subscription = new Nexora.Data.Billing.Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrderId = null,
            Status = Nexora.Business.Billing.BillingValues.Active,
            StartsAt = now,
            EndsAt = now.AddYears(100),
            CreatedAt = now,
            UpdatedAt = now
        };
        var entitlement = new Nexora.Data.Billing.Entitlement
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SubscriptionId = subscription.Id,
            PlanCodeSnapshot = freePlanPrice.Plan.Code,
            Status = Nexora.Business.Billing.BillingValues.Active,
            InterviewLimit = freePlanPrice.InterviewQuota,
            StartsAt = subscription.StartsAt,
            EndsAt = subscription.EndsAt,
            CreatedAt = now,
            UpdatedAt = now,
            ConcurrencyToken = Guid.NewGuid()
        };
        dbContext.Subscriptions.Add(subscription);
        dbContext.Entitlements.Add(entitlement);

        foreach (var feature in freePlanPrice.Features.Where(item =>
                     !string.Equals(item.FeatureDefinition.Code, Nexora.Business.Billing.FeatureValues.Interview, StringComparison.OrdinalIgnoreCase)))
        {
            dbContext.EntitlementFeatures.Add(new Nexora.Data.Billing.EntitlementFeature
            {
                Id = Guid.NewGuid(),
                EntitlementId = entitlement.Id,
                FeatureDefinitionId = feature.FeatureDefinitionId,
                FeatureCode = feature.FeatureDefinition.Code,
                IsEnabled = feature.IsEnabled,
                Limit = feature.Limit,
                CreatedAt = now,
                UpdatedAt = now,
                ConcurrencyToken = Guid.NewGuid()
            });
        }
    }

    private async Task<AuthSession> CreateSessionAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        var token = CreateRefreshToken(user.Id, timeProvider.GetUtcNow());
        dbContext.RefreshTokens.Add(token.Entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await CreateSessionResponseAsync(user, token.RawToken, token.Entity.ExpiresAt, cancellationToken);
    }

    private async Task<AuthSession> CreateSessionResponseAsync(ApplicationUser user, string rawRefreshToken, DateTimeOffset refreshExpiresAt, CancellationToken cancellationToken)
    {
        var roles = (await userManager.GetRolesAsync(user)).ToArray();
        var now = timeProvider.GetUtcNow();
        var accessExpiresAt = now.AddMinutes(_jwtOptions.AccessTokenMinutes);
        var profileName = user.Profile?.DisplayName ?? await dbContext.UserProfiles
            .Where(profile => profile.UserId == user.Id).Select(profile => profile.DisplayName)
            .SingleOrDefaultAsync(cancellationToken);
        return new AuthSession(new AuthenticatedUser(user.Id, user.Email ?? string.Empty, profileName, roles),
            CreateAccessToken(user, roles, now, accessExpiresAt), accessExpiresAt, rawRefreshToken, refreshExpiresAt);
    }

    private string CreateAccessToken(ApplicationUser user, IEnumerable<string> roles, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new(SecurityStampClaim, user.SecurityStamp ?? string.Empty)
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtOptions.SigningKey)), SecurityAlgorithms.HmacSha256);
        return new JwtSecurityTokenHandler().CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = _jwtOptions.Issuer,
            Audience = _jwtOptions.Audience,
            IssuedAt = issuedAt.UtcDateTime,
            NotBefore = issuedAt.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = credentials
        });
    }

    private (RefreshToken Entity, string RawToken) CreateRefreshToken(Guid userId, DateTimeOffset now)
    {
        var raw = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(64));
        return (new RefreshToken { Id = Guid.NewGuid(), UserId = userId, TokenHash = HashToken(raw), CreatedAt = now, ExpiresAt = now.AddDays(_jwtOptions.RefreshTokenDays) }, raw);
    }

    private async Task<AuthenticatedUser> MapUserAsync(ApplicationUser user) =>
        new(user.Id, user.Email ?? string.Empty, user.Profile?.DisplayName, (await userManager.GetRolesAsync(user)).ToArray());
    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));
    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
    private static string? NormalizeDisplayName(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static BusinessException InvalidEmailVerification() =>
        new("EMAIL_VERIFICATION_INVALID", "Liên kết xác minh email không hợp lệ hoặc đã hết hạn.", BusinessErrorKind.Validation);
    private static BusinessException InvalidRefreshToken() => new("INVALID_REFRESH_TOKEN", "Phiên đăng nhập không hợp lệ hoặc đã hết hạn.", BusinessErrorKind.Unauthorized);
    private static BusinessException UserNotFound() => new("USER_NOT_FOUND", "Không tìm thấy tài khoản.", BusinessErrorKind.NotFound);
    private static BusinessException IdentityValidation(IdentityResult result) =>
        new("IDENTITY_VALIDATION_FAILED", string.Join(" ", result.Errors.Select(error => error.Description)), BusinessErrorKind.Validation);
}
