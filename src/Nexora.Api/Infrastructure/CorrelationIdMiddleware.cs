using System.Text.RegularExpressions;

namespace Nexora.Api.Infrastructure;

public sealed partial class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";
    public async Task InvokeAsync(HttpContext context)
    {
        var candidate = context.Request.Headers[HeaderName].FirstOrDefault();
        var requestId = !string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 100 && SafePattern().IsMatch(candidate)
            ? candidate : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = requestId;
        context.Response.Headers[HeaderName] = requestId;
        await next(context);
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePattern();
}
