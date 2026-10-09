using Microsoft.AspNetCore.Http.Features;

namespace Nexora.Api.Infrastructure;

public sealed class JsonBodyBoundsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            const long maximumBytes = 1024 * 1024;
            if (context.Request.ContentLength > maximumBytes)
            {
                await ApiErrorWriter.WriteAsync(context, StatusCodes.Status413PayloadTooLarge,
                    "REQUEST_TOO_LARGE", "Nội dung yêu cầu quá lớn.");
                return;
            }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = Math.Min(feature.MaxRequestBodySize ?? maximumBytes, maximumBytes);
        }
        await next(context);
    }
}
