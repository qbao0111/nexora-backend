using Microsoft.AspNetCore.Authorization;
using Nexora.Business.Authorization;

namespace Nexora.Api.Authorization;

public sealed class OwnerRequirement : IAuthorizationRequirement;

public sealed class OwnerAuthorizationHandler(IOwnershipAuthorizer ownershipAuthorizer)
    : AuthorizationHandler<OwnerRequirement, IUserOwnedResource>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, OwnerRequirement requirement, IUserOwnedResource resource)
    {
        if (ownershipAuthorizer.IsOwner(context.User, resource)) context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
