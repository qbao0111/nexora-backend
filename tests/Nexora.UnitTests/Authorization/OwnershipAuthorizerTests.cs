using System.Security.Claims;
using Nexora.Business.Authorization;

namespace Nexora.UnitTests.Authorization;

public sealed class OwnershipAuthorizerTests
{
    private readonly OwnershipAuthorizer _authorizer = new();

    [Fact]
    public void IsOwnerReturnsTrueForSameUser()
    {
        var userId = Guid.NewGuid();
        Assert.True(_authorizer.IsOwner(Principal(userId), new OwnedResource(userId)));
    }

    [Fact]
    public void IsOwnerReturnsFalseForDifferentUserT02()
    {
        Assert.False(_authorizer.IsOwner(Principal(Guid.NewGuid()), new OwnedResource(Guid.NewGuid())));
    }

    [Fact]
    public void IsOwnerDoesNotLetAdminBypassPrivacy()
    {
        var principal = Principal(Guid.NewGuid(), "Admin");
        Assert.False(_authorizer.IsOwner(principal, new OwnedResource(Guid.NewGuid())));
    }

    private static ClaimsPrincipal Principal(Guid userId, string? role = null)
    {
        var claims = new List<Claim> { new("sub", userId.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed record OwnedResource(Guid UserId) : IUserOwnedResource;
}
