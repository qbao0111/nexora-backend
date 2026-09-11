using System.Diagnostics;
using System.Security.Claims;

namespace Nexora.Api.Infrastructure;

public sealed partial class RequestTelemetryMiddleware(RequestDelegate next, ILogger<RequestTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var responseStarted = 0L;
        context.Response.OnStarting(() =>
        {
            responseStarted = Stopwatch.GetTimestamp();
            return Task.CompletedTask;
        });
        await next(context);
        var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var ttfbMs = responseStarted == 0
            ? durationMs
            : Stopwatch.GetElapsedTime(started, responseStarted).TotalMilliseconds;
        var actorId = context.User.FindFirstValue("sub") ?? "anonymous";
        if (context.Response.StatusCode >= 500)
            ServerFailure(logger, context.TraceIdentifier, actorId, context.Request.Method, context.Request.Path, context.Response.StatusCode, ttfbMs, durationMs);
        else
            RequestCompleted(logger, context.TraceIdentifier, actorId, context.Request.Method, context.Request.Path, context.Response.StatusCode, ttfbMs, durationMs);
    }

    [LoggerMessage(LogLevel.Information,
        "Request {RequestId} completed for actor {ActorId}: {Method} {Path} returned {StatusCode} with TTFB {TtfbMs} ms in {DurationMs} ms")]
    private static partial void RequestCompleted(
        ILogger logger, string requestId, string actorId, string method, PathString path, int statusCode, double ttfbMs, double durationMs);

    [LoggerMessage(LogLevel.Error,
        "Request {RequestId} failed for actor {ActorId}: {Method} {Path} returned {StatusCode} with TTFB {TtfbMs} ms in {DurationMs} ms")]
    private static partial void ServerFailure(
        ILogger logger, string requestId, string actorId, string method, PathString path, int statusCode, double ttfbMs, double durationMs);
}
