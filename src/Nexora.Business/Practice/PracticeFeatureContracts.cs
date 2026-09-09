using Nexora.Business.Ai;

namespace Nexora.Business.Practice;

public sealed record ScenarioDimensionEvaluation(string Criterion, int Score, string Evidence, string Feedback);
public sealed record ScenarioEvaluationResult(
    int OverallScore,
    IReadOnlyCollection<ScenarioDimensionEvaluation> Dimensions,
    IReadOnlyCollection<string> Strengths,
    IReadOnlyCollection<string> Gaps,
    IReadOnlyCollection<string> RecommendedApproach,
    string Feedback,
    string? ScoreScale = null);

public static class PracticeFeatureValues
{
    public const string Draft = "draft";
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Published = "published";
    public const string Archived = "archived";

    public const string ScenarioEvaluationJob = "ScenarioEvaluationRequested";
    public const string StarEvaluationJob = "StarEvaluationRequested";
}

public sealed record ScenarioCategoryView(
    Guid Id,
    string Slug,
    string Name,
    string? Description,
    int SortOrder,
    bool IsActive);

public sealed record ScenarioCardView(
    Guid Id,
    string Slug,
    string Title,
    string Summary,
    string CategorySlug,
    string CategoryName,
    string Difficulty,
    string Competency,
    int EstimatedMinutes);

public sealed record ScenarioDetailView(
    Guid Id,
    string Slug,
    string Title,
    string Summary,
    string CategorySlug,
    string CategoryName,
    string Difficulty,
    string Competency,
    int EstimatedMinutes,
    string Content);

public sealed record ScenarioAttemptView(
    Guid Id,
    Guid ScenarioId,
    string ScenarioTitle,
    string Status,
    string? Answer,
    object? Evaluation,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ScenarioAttemptHistoryItem(
    Guid Id,
    int AttemptNumber,
    string Status,
    string? Answer,
    int? OverallScore,
    int? PreviousScore,
    int? ScoreDelta,
    bool? Improved,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record ScenarioAttemptComparison(
    int? CurrentScore,
    int? PreviousScore,
    int? Delta,
    bool? Improved);

public sealed record ScenarioAttemptHistoryView(
    Guid ScenarioId,
    string ScenarioSlug,
    string ScenarioTitle,
    string CategorySlug,
    string CategoryName,
    string Difficulty,
    string Competency,
    IReadOnlyCollection<ScenarioAttemptHistoryItem> Attempts,
    ScenarioAttemptComparison Comparison,
    int? LatestScore,
    int? BestScore);

public sealed record ScenarioTrackProgress(
    string CategorySlug,
    string CategoryName,
    int AttemptCount,
    int CompletedAttempts,
    double? AverageScore,
    int? LatestScore);

public sealed record ScenarioCompetencyProgress(
    string Competency,
    int AttemptCount,
    int CompletedAttempts,
    double? AverageScore,
    int? BestScore,
    int? LatestScore);

public sealed record ScenarioDifficultyProgress(
    string Difficulty,
    int AttemptCount,
    int CompletedAttempts,
    double? AverageScore);

public sealed record ScenarioProgressView(
    string RecommendedDifficulty,
    int AttemptCount,
    int CompletedAttempts,
    double? AverageScore,
    int? LatestScore,
    int? BestScore,
    IReadOnlyCollection<ScenarioTrackProgress> Tracks,
    IReadOnlyCollection<ScenarioCompetencyProgress> Competencies,
    IReadOnlyCollection<ScenarioDifficultyProgress> Difficulties);

public sealed record StarAttemptView(
    Guid Id,
    string Question,
    string Answer,
    string Status,
    object? Evaluation,
    string? ErrorCode,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record StarStoryListItemView(
    Guid Id,
    string Title,
    IReadOnlyCollection<string> Tags,
    int? LatestScore,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StarStoryDetailView(
    Guid Id,
    string Title,
    IReadOnlyCollection<string> Tags,
    string Situation,
    string Task,
    string Action,
    string Result,
    int? LatestScore,
    object? LatestEvaluation,
    DateTimeOffset? LatestEvaluatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record StarStoryPage(int Total, IReadOnlyCollection<StarStoryListItemView> Items);

public sealed record ProgressView(
    int CompletedInterviews,
    IReadOnlyCollection<RecentInterviewScore> RecentInterviewScores,
    double? AverageInterviewScore,
    ProgressStarAverages? StarAverages,
    int CompletedScenarios,
    double? AverageScenarioScore,
    int CompletedStarAttempts,
    IReadOnlyCollection<RecentActivity> RecentActivity);

public sealed record RecentInterviewScore(Guid InterviewId, int Score, DateTimeOffset CompletedAt);
public sealed record ProgressStarAverages(int Situation, int Task, int Action, int Result);
public sealed record RecentActivity(string Kind, Guid ResourceId, DateTimeOffset At);

public interface IScenarioService
{
    Task<IReadOnlyCollection<ScenarioCategoryView>> GetCategoriesAsync(CancellationToken cancellationToken);
    Task<ScenarioPage> GetScenariosAsync(string? category, string? difficulty, string? competency, string? search, int? page, int? pageSize, CancellationToken cancellationToken);
    Task<ScenarioDetailView> GetScenarioAsync(string slugOrId, CancellationToken cancellationToken);
    Task<ScenarioAttemptView> CreateAttemptAsync(Guid userId, Guid scenarioId, string idempotencyKey, CancellationToken cancellationToken);
    Task<ScenarioAttemptView> SubmitAttemptAsync(Guid userId, Guid attemptId, string answer, string idempotencyKey, CancellationToken cancellationToken);
    Task<ScenarioAttemptView> RetryAttemptAsync(Guid userId, Guid scenarioId, string idempotencyKey, CancellationToken cancellationToken);
    Task<ScenarioAttemptView> GetAttemptAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ScenarioAttemptView>> GetAttemptsAsync(Guid userId, CancellationToken cancellationToken);
    Task<ScenarioAttemptHistoryView> GetAttemptHistoryAsync(Guid userId, string slugOrId, CancellationToken cancellationToken);
    Task<ScenarioProgressView> GetProgressAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record ScenarioPage(int Total, IReadOnlyCollection<ScenarioCardView> Items);

public interface IStarAttemptService
{
    Task<StarAttemptView> CreateAsync(Guid userId, string question, string answer, string idempotencyKey, CancellationToken cancellationToken);
    Task<StarAttemptView> GetAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<StarAttemptView>> GetManyAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed record StarStoryCreateCommand(Guid SourceAttemptId, string? Title, IReadOnlyCollection<string>? Tags);
public sealed record StarStoryUpdateCommand(string Title, IReadOnlyCollection<string>? Tags, string Situation, string Task, string Action, string Result);

public interface IStarStoryService
{
    Task<StarStoryDetailView> CreateFromAttemptAsync(Guid userId, StarStoryCreateCommand command, string idempotencyKey, CancellationToken cancellationToken);
    Task<StarStoryPage> ListAsync(Guid userId, string? search, string? tag, int? page, int? pageSize, CancellationToken cancellationToken);
    Task<StarStoryDetailView> GetAsync(Guid userId, Guid storyId, CancellationToken cancellationToken);
    Task<StarStoryDetailView> UpdateAsync(Guid userId, Guid storyId, StarStoryUpdateCommand command, CancellationToken cancellationToken);
    Task<StarStoryDetailView> EvaluateAsync(Guid userId, Guid storyId, string idempotencyKey, CancellationToken cancellationToken);
}

public interface IProgressService
{
    Task<ProgressView> GetAsync(Guid userId, CancellationToken cancellationToken);
}

public interface IScenarioStarJobProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken);
}
