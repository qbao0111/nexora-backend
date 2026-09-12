using Nexora.Data.Practice;

namespace Nexora.Data.Identity;

public sealed class UserProfile
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? PrimaryResumeId { get; set; }
    public string? DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public ResumeRecord? PrimaryResume { get; set; }
}
