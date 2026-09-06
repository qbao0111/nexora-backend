using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Auth;
using Nexora.Business.Common;

namespace Nexora.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(
    IAuthService authService,
    LoginEmailRateLimiter loginEmailRateLimiter,
    IConfiguration configuration,
    IHostEnvironment environment) : ControllerBase
{
    private const string RefreshCookieName = "nexora.refresh";

    [AllowAnonymous, HttpPost("register"), EnableRateLimiting(RateLimitPolicies.Authentication)]
    public async Task<ActionResult<ApiResponse<AuthSessionResponse>>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        EnsureTrustedCookieOrigin();
        var session = await authService.RegisterAsync(new RegisterUserCommand(request.Email, request.Password, request.DisplayName), cancellationToken);
        WriteRefreshCookie(session);
        return StatusCode(201, new ApiResponse<AuthSessionResponse>(MapSession(session)));
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting(RateLimitPolicies.Authentication)]
    public async Task<ActionResult<ApiResponse<AuthSessionResponse>>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        EnsureTrustedCookieOrigin();
        using var lease = loginEmailRateLimiter.Acquire(request.Email);
        if (!lease.IsAcquired)
        {
            if (lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new ApiErrorEnvelope(new ApiError("RATE_LIMITED", "Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau.", HttpContext.TraceIdentifier)));
        }
        var session = await authService.LoginAsync(new LoginUserCommand(request.Email, request.Password), cancellationToken);
        WriteRefreshCookie(session);
        return Ok(new ApiResponse<AuthSessionResponse>(MapSession(session)));
    }

    [AllowAnonymous, HttpPost("refresh"), EnableRateLimiting(RateLimitPolicies.Refresh)]
    public async Task<ActionResult<ApiResponse<AuthSessionResponse>>> Refresh(CancellationToken cancellationToken)
    {
        EnsureTrustedCookieOrigin();
        var token = Request.Cookies[RefreshCookieName];
        if (string.IsNullOrWhiteSpace(token))
            throw new BusinessException("INVALID_REFRESH_TOKEN", "Phiên đăng nhập không hợp lệ hoặc đã hết hạn.", BusinessErrorKind.Unauthorized);
        var session = await authService.RefreshAsync(token, cancellationToken);
        WriteRefreshCookie(session);
        return Ok(new ApiResponse<AuthSessionResponse>(MapSession(session)));
    }

    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken cancellationToken)
    {
        EnsureTrustedCookieOrigin();
        var token = Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrWhiteSpace(token)) await authService.RevokeRefreshTokenAsync(token, cancellationToken);
        DeleteRefreshCookie();
        return NoContent();
    }

    [Authorize, HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken)
    {
        EnsureTrustedCookieOrigin();
        await authService.RevokeAllSessionsAsync(User.GetRequiredUserId(), cancellationToken);
        DeleteRefreshCookie();
        return NoContent();
    }

    private void WriteRefreshCookie(AuthSession session) => Response.Cookies.Append(RefreshCookieName, session.RefreshToken, CookieOptions(session.RefreshTokenExpiresAt));
    private void DeleteRefreshCookie() => Response.Cookies.Delete(RefreshCookieName, CookieOptions(DateTimeOffset.UnixEpoch));
    private CookieOptions CookieOptions(DateTimeOffset expiresAt)
    {
        var configuredSameSite = configuration["Authentication:RefreshCookie:SameSite"];
        var sameSite = Enum.TryParse<SameSiteMode>(configuredSameSite, ignoreCase: true, out var parsed)
            ? parsed
            : environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.Strict;
        var secure = configuration.GetValue<bool?>("Authentication:RefreshCookie:Secure") ?? !environment.IsDevelopment();
        if (sameSite == SameSiteMode.None && !secure)
            throw new InvalidOperationException("Authentication:RefreshCookie:Secure must be true when SameSite=None.");
        return new CookieOptions
        {
            HttpOnly = true,
            Secure = secure,
            SameSite = sameSite,
            Path = "/api/v1/auth",
            Expires = expiresAt,
            IsEssential = true
        };
    }

    private void EnsureTrustedCookieOrigin()
    {
        var origin = Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin)) return;
        if (IsSameOrigin(origin)) return;
        var allowedOrigins = configuration.GetSection("Frontend:AllowedOrigins").Get<string[]>() ?? [];
        if (!allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            throw new BusinessException("CSRF_ORIGIN_INVALID", "Nguồn trình duyệt không được phép.", BusinessErrorKind.Forbidden);
    }

    private bool IsSameOrigin(string origin)
    {
        if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri) &&
            Uri.TryCreate($"{Request.Scheme}://{Request.Host}", UriKind.Absolute, out var hostUri))
        {
            return string.Equals(originUri.Scheme, hostUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(originUri.Authority, hostUri.Authority, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static AuthSessionResponse MapSession(AuthSession session) =>
        new(session.AccessToken, session.AccessTokenExpiresAt, new UserResponse(session.User.Id, session.User.Email, session.User.DisplayName, session.User.Roles));
}
