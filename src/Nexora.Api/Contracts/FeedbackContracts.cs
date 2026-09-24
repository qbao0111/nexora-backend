using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Nexora.Business.Feedback;

namespace Nexora.Api.Contracts;

public sealed class FeedbackRequest
{
    [Range(1, 5)]
    public int Rating { get; init; }

    [StringLength(FeedbackRules.MaximumCommentLength)]
    public string? Comment { get; init; }

    [JsonPropertyName("allowPublicDisplay")]
    public bool AllowPublicDisplay { get; init; }
}

public sealed class AdminFeedbackActionRequest
{
    [StringLength(500)]
    public string? Reason { get; init; }
}

public sealed record FeedbackResponse(
    Guid Id,
    int Rating,
    string? Comment,
    bool AllowPublicDisplay,
    [property: JsonPropertyName("moderationStatus")] string Status,
    [property: JsonPropertyName("isFeatured")] bool Featured,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ModeratedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? DeletedAt);

public sealed record PublicFeedbackResponse(
    Guid Id,
    string DisplayName,
    int Rating,
    string Comment,
    DateTimeOffset PublishedAt,
    string? AvatarUrl);

public sealed record PublicFeedbackPageResponse(
    double? AverageRating,
    int RatingCount,
    IReadOnlyCollection<PublicFeedbackResponse> Items);

public sealed record AdminFeedbackResponse(
    Guid Id,
    Guid UserId,
    string Email,
    string? DisplayName,
    int Rating,
    string? Comment,
    bool AllowPublicDisplay,
    [property: JsonPropertyName("moderationStatus")] string Status,
    [property: JsonPropertyName("isFeatured")] bool Featured,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ModeratedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? DeletedAt,
    Guid? ModeratedByUserId = null);

public sealed record AdminFeedbackPageResponse(
    IReadOnlyCollection<AdminFeedbackResponse> Items,
    string? NextCursor,
    int PageSize);

public sealed record AdminFeedbackSummaryResponse(
    int Total,
    int Pending,
    int Approved,
    int Rejected,
    int Published,
    int Featured,
    double? AverageRating,
    IReadOnlyDictionary<int, int>? RatingDistribution = null);
