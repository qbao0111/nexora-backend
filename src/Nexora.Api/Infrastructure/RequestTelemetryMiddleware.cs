using System.Diagnostics;
using System.Security.Claims;

namespace Nexora.Api.Infrastructure;

public sealed partial class RequestTelemetryMiddleware(RequestDelegate next, ILogger<RequestTelemetryMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        await next(context);
        var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var actorId = context.User.FindFirstValue("sub") ?? "anonymous";
        if (context.Response.StatusCode >= 500)
            ServerFailure(logger, context.TraceIdentifier, actorId, context.Request.Method, context.Request.Path, context.Response.StatusCode, durationMs);
        else
            RequestCompleted(logger, context.TraceIdentifier, actorId, context.Request.Method, context.Request.Path, context.Response.StatusCode, durationMs);
    }

    [LoggerMessage(LogLevel.Information,
        "Request {RequestId} completed for actor {ActorId}: {Method} {Path} returned {StatusCode} in {DurationMs} ms")]
    private static partial void RequestCompleted(
        ILogger logger, string requestId, string actorId, string method, PathString path, int statusCode, double durationMs);

    [LoggerMessage(LogLevel.Error,
        "Request {RequestId} failed for actor {ActorId}: {Method} {Path} returned {StatusCode} in {DurationMs} ms")]
    private static partial void ServerFailure(
        ILogger logger, string requestId, string actorId, string method, PathString path, int statusCode, double durationMs);
}
