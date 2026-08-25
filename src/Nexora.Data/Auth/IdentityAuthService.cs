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
using Nexora.Data.Identity;
using Nexora.Data.Persistence;

namespace Nexora.Data.Auth;

public sealed class IdentityAuthService(
    UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager,
    NexoraDbContext dbContext,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider timeProvider) : IAuthService
{
    public const string SecurityStampClaim = "nexora:security_stamp";
    private readonly JwtOptions _jwtOptions = jwtOptions.Value;

    public async Task<AuthSession> RegisterAsync(RegisterUserCommand command, CancellationToken cancellationToken)
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
            SecurityStamp = Guid.NewGuid().ToString("N")
        };
        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var result = await userManager.CreateAsync(user, command.Password);
        if (!result.Succeeded) throw IdentityValidation(result);
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
        return await CreateSessionAsync(user, cancellationToken);
    }

    public async Task<AuthSession> LoginAsync(LoginUserCommand command, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(NormalizeEmail(command.Email));
        if (user is null || !(await signInManager.CheckPasswordSignInAsync(user, command.Password, true)).Succeeded)
        {
            throw new BusinessException("INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng.", BusinessErrorKind.Unauthorized);
        }
        return await CreateSessionAsync(user, cancellationToken);
    }

    public async Task<AuthSession> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var hash = HashToken(refreshToken);
        var existing = await dbContext.RefreshTokens.Include(token => token.User)
            .SingleOrDefaultAsync(token => token.TokenHash == hash, cancellationToken);
        if (existing is null || existing.ExpiresAt <= now) throw InvalidRefreshToken();

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
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken) ?? throw UserNotFound();
        return await MapUserAsync(user);
    }

    public async Task<AuthenticatedUser> UpdateProfileAsync(Guid userId, string? displayName, CancellationToken cancellationToken)
    {
        var user = await dbContext.Users.Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Id == userId, cancellationToken) ?? throw UserNotFound();
        var now = timeProvider.GetUtcNow();
        user.Profile ??= new UserProfile { Id = Guid.NewGuid(), UserId = user.Id, CreatedAt = now };
        user.Profile.DisplayName = NormalizeDisplayName(displayName);
        user.Profile.UpdatedAt = now;
        user.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return await MapUserAsync(user);
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
    private static BusinessException InvalidRefreshToken() => new("INVALID_REFRESH_TOKEN", "Phiên đăng nhập không hợp lệ hoặc đã hết hạn.", BusinessErrorKind.Unauthorized);
    private static BusinessException UserNotFound() => new("USER_NOT_FOUND", "Không tìm thấy tài khoản.", BusinessErrorKind.NotFound);
    private static BusinessException IdentityValidation(IdentityResult result) =>
        new("IDENTITY_VALIDATION_FAILED", string.Join(" ", result.Errors.Select(error => error.Description)), BusinessErrorKind.Validation);
}
