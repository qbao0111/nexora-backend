using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Nexora.Api.Infrastructure;

public static class TrustedForwarding
{
    public static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = configuration.GetValue("ReverseProxy:ForwardLimit", 1);
        if (options.ForwardLimit is < 1 or > 3)
            throw new InvalidOperationException("ReverseProxy:ForwardLimit must be between 1 and 3.");
        // Keep framework loopback trust; other hops require explicit observed IPs.
        foreach (var entry in configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [])
        {
            if (!IPAddress.TryParse(entry, out var address) ||
                address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                throw new InvalidOperationException("ReverseProxy:KnownProxies requires explicit IP addresses.");
            options.KnownProxies.Add(address);
        }
    }

    public static string ClientIp(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address?.IsIPv4MappedToIPv6 == true) address = address.MapToIPv4();
        return address?.ToString() ?? "unknown";
    }
}

public sealed class ForwardedHeaderBoundsMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        foreach (var name in new[] { "X-Forwarded-For", "X-Forwarded-Proto" })
        {
            var values = context.Request.Headers[name].ToString();
            if (values.Length > 4096 || values.Count(character => character == ',') > 15)
            {
                context.Request.Headers.Remove("X-Forwarded-For");
                context.Request.Headers.Remove("X-Forwarded-Proto");
                break;
            }
        }
        return next(context);
    }
}
