using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Nexora.Api.Infrastructure;

public static class RateLimitPolicies
{
    public const string Authentication = "authentication";
    public const string Refresh = "refresh";
    public const string Upload = "upload";
    public const string Checkout = "checkout";
    public const string AiJob = "ai-job";
    public const string Answer = "answer";
}

public sealed class LoginEmailRateLimiter(IConfiguration configuration) : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(email =>
    {
        if (configuration.GetValue<bool>("RateLimits:Disabled"))
            return RateLimitPartition.GetNoLimiter(email.Trim().ToUpperInvariant());

        return RateLimitPartition.GetFixedWindowLimiter(email.Trim().ToUpperInvariant(), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Positive(configuration, "RateLimits:LoginEmail:PermitLimit", 10),
            Window = TimeSpan.FromMinutes(Positive(configuration, "RateLimits:LoginEmail:WindowMinutes", 15)),
            QueueLimit = 0,
            AutoReplenishment = true
        });
    });

    public RateLimitLease Acquire(string email) => _limiter.AttemptAcquire(email);
    public void Dispose() => _limiter.Dispose();

    private static int Positive(IConfiguration configuration, string key, int fallback)
    {
        var value = configuration.GetValue(key, fallback);
        return value > 0 ? value : throw new InvalidOperationException($"{key} must be positive.");
    }
}

public sealed class FeatureOptions
{
    public const string SectionName = "Features";
    public bool Ai { get; set; } = true;
    public bool Payment { get; set; } = true;
    public bool Upload { get; set; } = true;
}

public static class HardeningExtensions
{
    public static IServiceCollection AddHardening(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<FeatureOptions>().Bind(configuration.GetSection(FeatureOptions.SectionName));
        services.AddSingleton<LoginEmailRateLimiter>();
        services.AddRateLimiter(options =>
        {
            AddFixedWindow(options, configuration, RateLimitPolicies.Authentication, "Authentication", 5, 15, ByIp);
            AddFixedWindow(options, configuration, RateLimitPolicies.Refresh, "Refresh", 30, 60, ByRefreshSession);
            AddFixedWindow(options, configuration, RateLimitPolicies.Upload, "Upload", 10, 60, ByUser);
            AddFixedWindow(options, configuration, RateLimitPolicies.Checkout, "Checkout", 5, 60, ByUser);
            AddFixedWindow(options, configuration, RateLimitPolicies.AiJob, "AiJob", 10, 60, ByUser);
            AddFixedWindow(options, configuration, RateLimitPolicies.Answer, "Answer", 20, 5,
                context => $"{ByUser(context)}:{context.Request.RouteValues["id"]}");
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(CultureInfo.InvariantCulture);
                await ApiErrorWriter.WriteAsync(context.HttpContext, StatusCodes.Status429TooManyRequests,
                    "RATE_LIMITED", "Bạn đã gửi quá nhiều yêu cầu. Vui lòng thử lại sau.");
            };
        });
        return services;
    }

    private static void AddFixedWindow(
        RateLimiterOptions options,
        IConfiguration configuration,
        string policy,
        string section,
        int defaultPermitLimit,
        int defaultWindowMinutes,
        Func<HttpContext, string> partitionKey)
    {
        if (configuration.GetValue<bool>("RateLimits:Disabled"))
        {
            options.AddPolicy(policy, context => RateLimitPartition.GetNoLimiter(partitionKey(context)));
            return;
        }

        var permitLimit = configuration.GetValue($"RateLimits:{section}:PermitLimit", defaultPermitLimit);
        var windowMinutes = configuration.GetValue($"RateLimits:{section}:WindowMinutes", defaultWindowMinutes);
        if (permitLimit <= 0 || windowMinutes <= 0) throw new InvalidOperationException($"RateLimits:{section} must contain positive values.");
        options.AddPolicy(policy, context => RateLimitPartition.GetFixedWindowLimiter(partitionKey(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = TimeSpan.FromMinutes(windowMinutes),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    }

    private static string ByIp(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    private static string ByUser(HttpContext context) => context.User.FindFirstValue("sub") ?? ByIp(context);
    private static string ByRefreshSession(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue("nexora.refresh", out var token) || string.IsNullOrWhiteSpace(token)) return ByIp(context);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }
}

public sealed class FeatureGateMiddleware(RequestDelegate next, IOptions<FeatureOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Method == HttpMethods.Post && DisabledFeature(context.Request.Path, options.Value) is { } feature)
        {
            await ApiErrorWriter.WriteAsync(context, StatusCodes.Status503ServiceUnavailable,
                "FEATURE_DISABLED", $"Tính năng {feature} đang tạm dừng.");
            return;
        }
        await next(context);
    }

    private static string? DisabledFeature(PathString path, FeatureOptions options)
    {
        if (!options.Upload && path == "/api/v1/uploads/presign") return "upload";
        if (!options.Payment && path == "/api/v1/checkout-sessions") return "payment";
        if (!options.Ai && (path == "/api/v1/resume-analyses" || path.StartsWithSegments("/api/v1/interviews"))) return "AI";
        return null;
    }
}
