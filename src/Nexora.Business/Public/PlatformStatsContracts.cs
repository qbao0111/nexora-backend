namespace Nexora.Business.Platform;

public sealed record PlatformStatsView(
    int UserCount,
    int CompletedInterviewCount,
    int CompletedCvAnalysisCount,
    double? AverageRating,
    int RatingCount);

public interface IPlatformStatsService
{
    Task<PlatformStatsView> GetAsync(CancellationToken cancellationToken);
}
