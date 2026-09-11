namespace Nexora.Api.Contracts;

public sealed record NextPracticeRecommendationResponse(
    string Reason,
    string ActivityType,
    Guid? ResourceId,
    int EstimatedMinutes,
    int Priority);
