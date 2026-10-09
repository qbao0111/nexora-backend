using System.Globalization;
using System.Threading.RateLimiting;

namespace Nexora.Api.Infrastructure;

// Separate from endpoint policies: no principal/route policy is evaluated until
// after authentication. This gate protects JWT security-stamp DB work itself.
public sealed class PreAuthenticationRateLimiter(IConfiguration configuration) : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter = configuration.GetValue<bool>("RateLimits:Disabled")
        ? PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetNoLimiter("disabled"))
        : PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                context.Request.Path.StartsWithSegments("/api/v1/auth/refresh") ||
                context.Request.Path.StartsWithSegments("/api/v1/auth/mobile/refresh")
                    ? RateLimitPartition.GetFixedWindowLimiter(TrustedForwarding.ClientIp(context), _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = configuration.GetValue("RateLimits:RefreshIp:PermitLimit", 120),
                        Window = TimeSpan.FromHours(1), QueueLimit = 0, AutoReplenishment = true
                    }) : RateLimitPartition.GetNoLimiter("not-refresh")),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(TrustedForwarding.ClientIp(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = configuration.GetValue("RateLimits:Burst:PermitLimit", 300),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                })),
            PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                RateLimitPartition.GetConcurrencyLimiter("global", _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = configuration.GetValue("RateLimits:ConcurrentRequests", 100), QueueLimit = 0
                })));

    public ValueTask<RateLimitLease> AcquireAsync(HttpContext context) => _limiter.AcquireAsync(context, cancellationToken: context.RequestAborted);
    public void Dispose() => _limiter.Dispose();
}

public sealed class PreAuthenticationRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, PreAuthenticationRateLimiter limiter)
    {
        using var lease = await limiter.AcquireAsync(context);
        if (!lease.IsAcquired)
        {
            var retry = lease.TryGetMetadata(MetadataName.RetryAfter, out var duration) ? Math.Ceiling(duration.TotalSeconds) : 1;
            context.Response.Headers.RetryAfter = Math.Max(1, retry).ToString(CultureInfo.InvariantCulture);
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status429TooManyRequests,
                "RATE_LIMITED", "Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau.");
            return;
        }
        await next(context);
    }
}
