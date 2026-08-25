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
public sealed class AuthController(IAuthService authService, LoginEmailRateLimiter loginEmailRateLimiter) : ControllerBase
{
    private const string RefreshCookieName = "nexora.refresh";

    [AllowAnonymous, HttpPost("register"), EnableRateLimiting(RateLimitPolicies.Authentication)]
    public async Task<ActionResult<ApiResponse<AuthSessionResponse>>> Register(RegisterRequest request, CancellationToken cancellationToken)
    {
        var session = await authService.RegisterAsync(new RegisterUserCommand(request.Email, request.Password, request.DisplayName), cancellationToken);
        WriteRefreshCookie(session);
        return StatusCode(201, new ApiResponse<AuthSessionResponse>(MapSession(session)));
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting(RateLimitPolicies.Authentication)]
    public async Task<ActionResult<ApiResponse<AuthSessionResponse>>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
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
        var token = Request.Cookies[RefreshCookieName];
        if (!string.IsNullOrWhiteSpace(token)) await authService.RevokeRefreshTokenAsync(token, cancellationToken);
        DeleteRefreshCookie();
        return NoContent();
    }

    [Authorize, HttpPost("logout-all")]
    public async Task<IActionResult> LogoutAll(CancellationToken cancellationToken)
    {
        await authService.RevokeAllSessionsAsync(User.GetRequiredUserId(), cancellationToken);
        DeleteRefreshCookie();
        return NoContent();
    }

    private void WriteRefreshCookie(AuthSession session) => Response.Cookies.Append(RefreshCookieName, session.RefreshToken, CookieOptions(session.RefreshTokenExpiresAt));
    private void DeleteRefreshCookie() => Response.Cookies.Delete(RefreshCookieName, CookieOptions(DateTimeOffset.UnixEpoch));
    private static CookieOptions CookieOptions(DateTimeOffset expiresAt) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/v1/auth",
        Expires = expiresAt,
        IsEssential = true
    };
    private static AuthSessionResponse MapSession(AuthSession session) =>
        new(session.AccessToken, session.AccessTokenExpiresAt, new UserResponse(session.User.Id, session.User.Email, session.User.DisplayName, session.User.Roles));
}
