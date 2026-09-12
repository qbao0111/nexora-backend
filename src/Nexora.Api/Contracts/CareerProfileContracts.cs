namespace Nexora.Api.Contracts;

public sealed record SetPrimaryResumeRequest(Guid ResumeId);

public sealed record PrimaryResumeResponse(
    Guid Id,
    string FileName,
    string Status,
    DateTimeOffset CreatedAt,
    ResumeAnalysisSummaryResponse? LatestAnalysis = null);

public sealed record ResumeAnalysisSummaryResponse(
    Guid Id,
    string Mode,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record CareerProfileIdentityResponse(
    Guid UserId,
    string Email,
    string? DisplayName,
    string? AvatarUrl);

public sealed record CareerProfileGoalResponse(
    Guid Id,
    string TargetRole,
    string Seniority,
    string? Industry,
    string? TargetCompany,
    DateOnly? TargetDate,
    bool Active);

public sealed record CareerProfileCompetencyResponse(
    string Code,
    string Name,
    string Category,
    int Score,
    int EvidenceCount);

public sealed record CareerProfileWeaknessResponse(
    string SourceType,
    string Label,
    DateTimeOffset LatestEvidenceAt);

public sealed record CareerProfileSkillSummaryResponse(
    IReadOnlyCollection<CareerProfileCompetencyResponse> TopCompetencies,
    IReadOnlyCollection<CareerProfileWeaknessResponse> TopWeaknessSignals);

public sealed record CareerProfileLearningPathResponse(
    Guid Id,
    string Status,
    int PendingActivityCount,
    int CompletedActivityCount);

public sealed record CareerProfileOnboardingResponse(
    bool HasPrimaryResume,
    bool HasActiveCareerGoal,
    bool IsComplete);

public sealed record CareerProfileResponse(
    CareerProfileIdentityResponse Profile,
    PrimaryResumeResponse? PrimaryResume,
    CareerProfileGoalResponse? ActiveCareerGoal,
    CareerProfileSkillSummaryResponse SkillProfileSummary,
    CareerProfileLearningPathResponse? LearningPath,
    CareerProfileOnboardingResponse Onboarding);
