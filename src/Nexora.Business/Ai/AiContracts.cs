using System.Text.Json;

namespace Nexora.Business.Ai;

public sealed record AiRequest(
    string Purpose,
    string PromptVersion,
    string ModelVersion,
    string RubricVersion,
    string SchemaVersion,
    string UntrustedInput,
    JsonDocument OutputSchema,
    int MaxOutputTokens,
    string CorrelationId);

public interface IAiProvider
{
    /// <summary>
    /// Identifies the configured model/provider revision used for persisted AI results.
    /// </summary>
    string ModelVersion { get; }

    Task<T> GenerateStructuredAsync<T>(AiRequest request, CancellationToken cancellationToken);
}

public enum AiProviderFailureKind
{
    Configuration,
    Authentication,
    RateLimited,
    Timeout,
    Unavailable,
    InvalidResponse
}

public sealed class AiProviderException(AiProviderFailureKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public AiProviderFailureKind Kind { get; } = kind;
}

public sealed record GeneratedQuestion(string Content);
public sealed record ResumeAnalysisOutput(IReadOnlyCollection<string> Strengths, IReadOnlyCollection<string> Gaps, IReadOnlyCollection<string> Recommendations);
public sealed record RubricScore(string Criterion, int Score, string Evidence);
public sealed record StarComponentEvaluation(int Score, bool Detected, string Evidence, string Feedback);
public sealed record StarEvaluation(
    bool Applicable,
    int? OverallScore,
    StarComponentEvaluation? Situation,
    StarComponentEvaluation? Task,
    StarComponentEvaluation? Action,
    StarComponentEvaluation? Result,
    IReadOnlyCollection<string> MissingElements,
    IReadOnlyCollection<string> Strengths,
    IReadOnlyCollection<string> CoachingTips);
public sealed record StarComponentAverages(int Situation, int Task, int Action, int Result);
public sealed record StarReportSummary(
    int ApplicableAnswers,
    int AverageScore,
    StarComponentAverages ComponentAverages,
    string StrongestComponent,
    string WeakestComponent,
    IReadOnlyCollection<string> RecurringIssues,
    IReadOnlyCollection<string> CoachingPriorities);
public sealed record AnswerEvaluation(IReadOnlyCollection<RubricScore> Scores, string Feedback, StarEvaluation? Star = null);
public sealed record InterviewReportOutput(
    IReadOnlyCollection<RubricScore> Scores,
    IReadOnlyCollection<string> Strengths,
    IReadOnlyCollection<string> Gaps,
    IReadOnlyCollection<string> ActionPlan);
