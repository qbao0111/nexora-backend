using System.Security.Claims;

namespace Nexora.Business.Authorization;

public static class AuthorizationPolicies
{
    public const string Owner = "Owner";
    public const string Admin = "Admin";
}

public interface IUserOwnedResource { Guid UserId { get; } }
public interface IOwnershipAuthorizer { bool IsOwner(ClaimsPrincipal principal, IUserOwnedResource resource); }

public sealed class OwnershipAuthorizer : IOwnershipAuthorizer
{
    public bool IsOwner(ClaimsPrincipal principal, IUserOwnedResource resource)
    {
        var subject = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(subject, out var currentUserId) && currentUserId == resource.UserId;
    }
}
