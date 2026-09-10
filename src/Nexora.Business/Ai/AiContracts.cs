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
    string CorrelationId,
    string? Instructions = null,
    AiReasoningEffortOverride? ReasoningEffortOverride = null);

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

public enum AiReasoningEffortOverride
{
    Low
}

public enum AiProviderRetryHint
{
    None,
    LowerReasoningEffort,
    OutputTruncated
}

public sealed class AiProviderException(
    AiProviderFailureKind kind,
    string message,
    Exception? innerException = null,
    AiProviderRetryHint retryHint = AiProviderRetryHint.None)
    : Exception(message, innerException)
{
    public AiProviderFailureKind Kind { get; } = kind;
    public AiProviderRetryHint RetryHint { get; } = retryHint;
}

public sealed record GeneratedQuestion(string Content);
/// <summary>
/// Provider-neutral CV analysis result. The active shape is selected by the
/// explicit <see cref="Nexora.Business.Practice.ResumeAnalysisMode"/> carried in the operation
/// context; job-targeted results use <see cref="MatchScore"/>, while
/// field-benchmark results use <see cref="ReadinessScore"/>.
///
/// The first three parameters remain in their historical order so deterministic
/// test providers and previously compiled callers can continue to construct the
/// common coaching collections while the v2 fields are introduced additively.
/// </summary>
public sealed record ResumeAnalysisOutput(
    IReadOnlyCollection<string> Strengths,
    IReadOnlyCollection<string> Gaps,
    IReadOnlyCollection<string> Recommendations,
    int? MatchScore = null,
    int? ReadinessScore = null,
    string? Summary = null,
    IReadOnlyCollection<string>? MatchedKeywordsOrSkills = null,
    IReadOnlyCollection<string>? MissingKeywordsOrSkills = null,
    IReadOnlyCollection<string>? SectionFeedback = null,
    IReadOnlyDictionary<string, int>? Breakdown = null,
    string? Mode = null);
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
    IReadOnlyCollection<string> CoachingTips,
    string? ScoreScale = null);
public sealed record StarComponentAverages(int Situation, int Task, int Action, int Result);
public sealed record StarReportSummary(
    int ApplicableAnswers,
    int AverageScore,
    StarComponentAverages ComponentAverages,
    string StrongestComponent,
    string WeakestComponent,
    IReadOnlyCollection<string> RecurringIssues,
    IReadOnlyCollection<string> CoachingPriorities);
public sealed record AnswerCoachingOutput(
    IReadOnlyCollection<string> Strengths,
    IReadOnlyCollection<string> Improvements,
    string ImprovedAnswer);
public sealed record AnswerEvaluation(
    IReadOnlyCollection<RubricScore> Scores,
    string Feedback,
    StarEvaluation? Star = null,
    string? ScoreScale = null,
    IReadOnlyCollection<string>? Strengths = null,
    IReadOnlyCollection<string>? Improvements = null,
    string? ImprovedAnswer = null);
public sealed record InterviewReportOutput(
    IReadOnlyCollection<RubricScore> Scores,
    IReadOnlyCollection<string> Strengths,
    IReadOnlyCollection<string> Gaps,
    IReadOnlyCollection<string> ActionPlan,
    string? ScoreScale = null);

public sealed record AiValidationResult<T>(
    bool IsValid,
    T? NormalizedValue,
    string? FailureReason = null,
    string? ValidationStage = null,
    bool Repairable = false)
{
#pragma warning disable CA1000
    public static AiValidationResult<T> Success(T value) =>
        new(true, value);

    public static AiValidationResult<T> Failure(string failureReason, string validationStage, bool repairable = false) =>
        new(false, default, failureReason, validationStage, repairable);
#pragma warning restore CA1000
}

public sealed record AiOperationContext(
    string CorrelationId,
    Guid? UserId = null,
    bool? ExpectedStar = null,
    string? InterviewType = null,
    int? TargetQuestionCount = null,
    IReadOnlyDictionary<string, string>? Metadata = null,
    string? CandidateAnswer = null);

public sealed record AiExecutionResult<T>(
    T Value,
    string ModelVersion,
    string PromptVersion,
    string SchemaVersion,
    string RubricVersion,
    bool RepairUsed,
    int Attempts,
    long LatencyMs);

public interface IStructuredAiExecutor
{
    Task<AiExecutionResult<T>> ExecuteAsync<T>(
        AiOperationDefinition<T> operation,
        string untrustedInput,
        AiOperationContext context,
        CancellationToken cancellationToken);
}
