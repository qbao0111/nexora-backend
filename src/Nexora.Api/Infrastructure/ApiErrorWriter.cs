using Nexora.Api.Contracts;

namespace Nexora.Api.Infrastructure;

public static class ApiErrorWriter
{
    public static Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(new ApiErrorEnvelope(new ApiError(code, message, context.TraceIdentifier)), context.RequestAborted);
    }
}
