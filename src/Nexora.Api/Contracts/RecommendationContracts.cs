namespace Nexora.Api.Contracts;

public sealed record NextPracticeActionResponse(
    string Type,
    string Reason,
    Guid? SourceInterviewId,
    Guid? SourceQuestionId,
    string? FocusTopic,
    string? SuggestedInterviewType);

public sealed record NextPracticeRecommendationRationaleResponse(
    string CompetencyName,
    int EvidenceCount,
    bool HasMoreRecentlyPracticedPeer);

public sealed record NextPracticeRecommendationResponse(
    string Reason,
    string ActivityType,
    Guid? ResourceId,
    int EstimatedMinutes,
    int Priority,
    NextPracticeActionResponse? Action = null,
    NextPracticeRecommendationRationaleResponse? Rationale = null);
