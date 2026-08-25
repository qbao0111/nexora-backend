using System.Security.Claims;
using Nexora.Business.Common;

namespace Nexora.Api.Infrastructure;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetRequiredUserId(this ClaimsPrincipal principal)
    {
        var value = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(value, out var userId)) return userId;
        throw new BusinessException("UNAUTHENTICATED", "Bạn cần đăng nhập để tiếp tục.", BusinessErrorKind.Unauthorized);
    }
}
