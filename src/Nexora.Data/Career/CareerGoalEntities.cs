using Nexora.Data.Identity;
using Nexora.Data.Practice;

namespace Nexora.Data.Career;

public sealed class CareerGoal
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string TargetRole { get; set; } = string.Empty;
    public string Seniority { get; set; } = string.Empty;
    public string? Industry { get; set; }
    public string? TargetCompany { get; set; }
    public Guid? TargetJobDescriptionId { get; set; }
    public DateOnly? TargetDate { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public JobDescription? TargetJobDescription { get; set; }
}
