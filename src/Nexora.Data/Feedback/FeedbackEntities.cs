using Nexora.Data.Identity;

namespace Nexora.Data.Feedback;

public sealed class ProductFeedback
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public int Rating { get; set; }
    public string? Comment { get; set; }
    public bool Consent { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool Featured { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ModeratedAt { get; set; }
    public Guid? ModeratedByUserId { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public ApplicationUser User { get; set; } = null!;
    public ApplicationUser? ModeratedByUser { get; set; }
}
