using System.Globalization;
using System.Threading.RateLimiting;
using Nexora.Api.Realtime;

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
                RateLimitPartition.GetFixedWindowLimiter((IsHealth(context) ? "health:" : "traffic:") + TrustedForwarding.ClientIp(context), _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = configuration.GetValue(IsHealth(context) ? "RateLimits:Health:BurstPermitLimit" : "RateLimits:Burst:PermitLimit", IsHealth(context) ? 60 : 300),
                    Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                })),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                IsRealtime(context) || IsHealth(context)
                    ? RateLimitPartition.GetConcurrencyLimiter(Category(context) + ":" + TrustedForwarding.ClientIp(context), _ => new ConcurrencyLimiterOptions
                    {
                        PermitLimit = configuration.GetValue(IsHealth(context) ? "RateLimits:Health:ConcurrentPerIp" : "RateLimits:Realtime:ConcurrentPerIp", IsHealth(context) ? 2 : 20),
                        QueueLimit = 0
                    }) : RateLimitPartition.GetNoLimiter("regular-http")),
            PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetConcurrencyLimiter(Category(context), _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = configuration.GetValue(IsHealth(context) ? "RateLimits:Health:ConcurrentRequests" :
                        IsRealtime(context) ? "RateLimits:Realtime:ConcurrentRequests" : "RateLimits:ConcurrentRequests", IsHealth(context) ? 10 : 100),
                    QueueLimit = 0
                })));

    // GET transports include long polls/SSE. Negotiate/send/DELETE are short
    // HTTP requests and must remain usable while all transport slots are held.
    // Treat every WebSocket upgrade as long-lived, including future endpoints.
    // No client-supplied transport query is trusted.
    private static bool IsRealtime(HttpContext context) => context.WebSockets.IsWebSocketRequest ||
        HttpMethods.IsGet(context.Request.Method) && context.Request.Path.StartsWithSegments(RealtimeHub.Path);
    private static bool IsHealth(HttpContext context) => HttpMethods.IsGet(context.Request.Method) &&
        (context.Request.Path == "/health/live" || context.Request.Path == "/api/v1/health" || context.Request.Path == "/api/v1/health/operations");
    private static string Category(HttpContext context) => IsHealth(context) ? "health" : IsRealtime(context) ? "realtime" : "http";

    public ValueTask<RateLimitLease> AcquireAsync(HttpContext context) => _limiter.AcquireAsync(context, cancellationToken: context.RequestAborted);
    public RateLimiterStatistics? GetStatistics(HttpContext context) => _limiter.GetStatistics(context);
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
