namespace Nexora.Api.Contracts;

public sealed record PlatformStatsResponse(
    int UserCount,
    int CompletedInterviewCount,
    int CompletedCvAnalysisCount,
    double? AverageRating,
    int RatingCount);
