namespace Nexora.Business.Feedback;

public static class FeedbackValues
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
}

public static class FeedbackRules
{
    public const int MaximumCommentLength = 1_000;
    public const int MaximumPublicLimit = 20;
    public const int DefaultPublicLimit = 12;
    public const int MaximumAdminPageSize = 100;
}

public sealed record FeedbackWriteCommand(int Rating, string? Comment, bool Consent);

public sealed record ProductFeedbackView(
    Guid Id,
    int Rating,
    string? Comment,
    bool Consent,
    string Status,
    bool Featured,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ModeratedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? DeletedAt,
    Guid? ModeratedByUserId = null);

public sealed record PublicFeedbackView(
    Guid Id,
    string DisplayName,
    int Rating,
    string Comment,
    DateTimeOffset PublishedAt);

public sealed record ProductFeedbackAdminView(
    Guid Id,
    Guid UserId,
    string Email,
    string? DisplayName,
    int Rating,
    string? Comment,
    bool Consent,
    string Status,
    bool Featured,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ModeratedAt,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? DeletedAt,
    Guid? ModeratedByUserId = null);

public sealed record ProductFeedbackAdminQuery(
    string? Status,
    string? Search,
    bool? Featured,
    bool? Consent,
    bool IncludeDeleted,
    string? Cursor,
    int PageSize,
    int? Rating = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null);

public sealed record ProductFeedbackAdminPage(
    IReadOnlyCollection<ProductFeedbackAdminView> Items,
    string? NextCursor,
    int PageSize);

public sealed record ProductFeedbackSummary(
    int Total,
    int Pending,
    int Approved,
    int Rejected,
    int Published,
    int Featured,
    double? AverageRating,
    IReadOnlyDictionary<int, int>? RatingDistribution = null);

public interface IProductFeedbackService
{
    Task<ProductFeedbackView?> GetCurrentAsync(Guid userId, CancellationToken cancellationToken);
    Task<ProductFeedbackView> UpsertAsync(Guid userId, FeedbackWriteCommand command, CancellationToken cancellationToken);
    Task DeleteCurrentAsync(Guid userId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<PublicFeedbackView>> GetPublicAsync(int limit, CancellationToken cancellationToken);
    Task<ProductFeedbackAdminPage> GetAdminPageAsync(ProductFeedbackAdminQuery query, CancellationToken cancellationToken);
    Task<ProductFeedbackSummary> GetSummaryAsync(CancellationToken cancellationToken);
    Task<ProductFeedbackAdminView> ApproveAsync(Guid adminUserId, Guid feedbackId, string? reason, CancellationToken cancellationToken);
    Task<ProductFeedbackAdminView> RejectAsync(Guid adminUserId, Guid feedbackId, string? reason, CancellationToken cancellationToken);
    Task<ProductFeedbackAdminView> FeatureAsync(Guid adminUserId, Guid feedbackId, string? reason, CancellationToken cancellationToken);
    Task<ProductFeedbackAdminView> UnfeatureAsync(Guid adminUserId, Guid feedbackId, string? reason, CancellationToken cancellationToken);
}
