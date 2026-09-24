using Nexora.Business.Skills;

namespace Nexora.Business.Career;

public sealed record PrimaryResumeSummary(
    Guid Id,
    string FileName,
    string Status,
    DateTimeOffset CreatedAt,
    ResumeAnalysisSummary? LatestAnalysis = null);

public sealed record ResumeAnalysisSummary(
    Guid Id,
    string Mode,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record CareerProfileIdentityView(
    Guid UserId,
    string Email,
    string? DisplayName,
    int? YearsOfExperience,
    Guid? AvatarId);

public sealed record CareerProfileGoalView(
    Guid Id,
    string TargetRole,
    string Seniority,
    string? Industry,
    string? TargetCompany,
    DateOnly? TargetDate,
    bool Active);

public sealed record CareerProfileSkillSummary(
    IReadOnlyCollection<SkillProfileCompetency> TopCompetencies,
    IReadOnlyCollection<SkillProfileWeaknessSignal> TopWeaknessSignals);

public sealed record CareerProfileLearningPathSummary(
    Guid Id,
    string Status,
    int PendingActivityCount,
    int CompletedActivityCount);

public sealed record CareerProfileOnboardingSummary(
    bool HasDisplayName,
    bool HasYearsOfExperience,
    bool HasPrimaryResume,
    bool HasActiveCareerGoal,
    bool IsComplete);

public sealed record CareerProfileView(
    CareerProfileIdentityView Profile,
    PrimaryResumeSummary? PrimaryResume,
    CareerProfileGoalView? ActiveCareerGoal,
    CareerProfileSkillSummary SkillProfileSummary,
    CareerProfileLearningPathSummary? LearningPath,
    CareerProfileOnboardingSummary Onboarding);

public interface ICareerProfileService
{
    Task<PrimaryResumeSummary?> SetPrimaryResumeAsync(
        Guid userId,
        Guid? resumeId,
        CancellationToken cancellationToken);

    Task<CareerProfileView> GetAsync(
        Guid userId,
        CancellationToken cancellationToken);
}
