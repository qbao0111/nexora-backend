using Nexora.Api.Observability;
using Nexora.Business.Common;

namespace Nexora.Api.Infrastructure;

public sealed partial class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger,
    IApiSentryReporter sentryReporter)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (BusinessException exception)
        {
            BusinessFailure(logger, context.TraceIdentifier, exception.Code);
            var status = exception.Kind switch
            {
                BusinessErrorKind.Validation => 400,
                BusinessErrorKind.Unauthorized => 401,
                BusinessErrorKind.Forbidden => 403,
                BusinessErrorKind.NotFound => 404,
                BusinessErrorKind.Conflict => 409,
                _ => 503
            };
            if (exception.Kind == BusinessErrorKind.ExternalFailure)
            {
                sentryReporter.Capture(
                    exception,
                    context.TraceIdentifier,
                    context.Request.Method,
                    context.Request.Path.Value ?? "/",
                    status);
            }
            await ApiErrorWriter.WriteAsync(context, status, exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            ClientCancelled(logger, context.TraceIdentifier);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode is >= 400 and < 500)
        {
            InvalidRequest(logger, context.TraceIdentifier, exception.StatusCode);
            if (context.RequestAborted.IsCancellationRequested)
                return;
            if (context.Response.HasStarted)
            {
                context.Abort();
                return;
            }

            try
            {
                await ApiErrorWriter.WriteAsync(context, exception.StatusCode, "BAD_HTTP_REQUEST", "Yêu cầu HTTP không hợp lệ.");
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                ClientCancelled(logger, context.TraceIdentifier);
            }
            catch (IOException)
            {
                ClientCancelled(logger, context.TraceIdentifier);
                context.Abort();
            }
        }
        catch (Exception exception)
        {
            UnhandledFailure(logger, exception, context.TraceIdentifier);
            sentryReporter.Capture(
                exception,
                context.TraceIdentifier,
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                StatusCodes.Status500InternalServerError);
            await ApiErrorWriter.WriteAsync(context, 500, "INTERNAL_ERROR", "Đã xảy ra lỗi. Vui lòng thử lại sau.");
        }
    }

    [LoggerMessage(LogLevel.Warning, "Request {RequestId} failed with business error {ErrorCode}")]
    private static partial void BusinessFailure(ILogger logger, string requestId, string errorCode);

    [LoggerMessage(LogLevel.Information, "Request {RequestId} was cancelled by the client")]
    private static partial void ClientCancelled(ILogger logger, string requestId);

    [LoggerMessage(LogLevel.Information, "Request {RequestId} rejected with HTTP status {StatusCode}")]
    private static partial void InvalidRequest(ILogger logger, string requestId, int statusCode);

    [LoggerMessage(LogLevel.Error, "Unhandled error for request {RequestId}")]
    private static partial void UnhandledFailure(ILogger logger, Exception exception, string requestId);
}
