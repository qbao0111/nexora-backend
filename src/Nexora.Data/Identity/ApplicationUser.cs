using Microsoft.AspNetCore.Identity;

namespace Nexora.Data.Identity;

public sealed class ApplicationUser : IdentityUser<Guid>
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public UserProfile? Profile { get; set; }
    public ICollection<RefreshToken> RefreshTokens { get; } = [];
}
