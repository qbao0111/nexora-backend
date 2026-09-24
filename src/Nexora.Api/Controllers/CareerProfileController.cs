using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Career;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/me")]
public sealed class CareerProfileController(ICareerProfileService careerProfileService) : ControllerBase
{
    [HttpPut("primary-resume")]
    public async Task<ActionResult<ApiResponse<PrimaryResumeResponse?>>> SetPrimaryResume(
        SetPrimaryResumeRequest request,
        CancellationToken cancellationToken)
    {
        var primaryResume = await careerProfileService.SetPrimaryResumeAsync(
            User.GetRequiredUserId(), request.ResumeId, cancellationToken);
        return Ok(new ApiResponse<PrimaryResumeResponse?>(primaryResume is null ? null : Map(primaryResume)));
    }

    [HttpGet("career-profile")]
    public async Task<ActionResult<ApiResponse<CareerProfileResponse>>> GetCareerProfile(CancellationToken cancellationToken)
    {
        var profile = await careerProfileService.GetAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<CareerProfileResponse>(Map(profile)));
    }

    private static CareerProfileResponse Map(CareerProfileView profile) => new(
        new CareerProfileIdentityResponse(
            profile.Profile.UserId,
            profile.Profile.Email,
            profile.Profile.DisplayName,
            profile.Profile.YearsOfExperience,
            AvatarUrls.For(profile.Profile.AvatarId)),
        profile.PrimaryResume is null ? null : Map(profile.PrimaryResume),
        profile.ActiveCareerGoal is null ? null : new CareerProfileGoalResponse(
            profile.ActiveCareerGoal.Id,
            profile.ActiveCareerGoal.TargetRole,
            profile.ActiveCareerGoal.Seniority,
            profile.ActiveCareerGoal.Industry,
            profile.ActiveCareerGoal.TargetCompany,
            profile.ActiveCareerGoal.TargetDate,
            profile.ActiveCareerGoal.Active),
        new CareerProfileSkillSummaryResponse(
            profile.SkillProfileSummary.TopCompetencies.Select(item => new CareerProfileCompetencyResponse(
                item.Code, item.Name, item.Category, item.Score, item.EvidenceCount)).ToArray(),
            profile.SkillProfileSummary.TopWeaknessSignals.Select(item => new CareerProfileWeaknessResponse(
                item.SourceType, item.Label, item.LatestEvidenceAt)).ToArray()),
        profile.LearningPath is null ? null : new CareerProfileLearningPathResponse(
            profile.LearningPath.Id,
            profile.LearningPath.Status,
            profile.LearningPath.PendingActivityCount,
            profile.LearningPath.CompletedActivityCount),
        new CareerProfileOnboardingResponse(
            profile.Onboarding.HasDisplayName,
            profile.Onboarding.HasYearsOfExperience,
            profile.Onboarding.HasPrimaryResume,
            profile.Onboarding.HasActiveCareerGoal,
            profile.Onboarding.IsComplete));

    private static PrimaryResumeResponse Map(PrimaryResumeSummary resume) => new(
        resume.Id,
        resume.FileName,
        resume.Status,
        resume.CreatedAt,
        resume.LatestAnalysis is null ? null : new ResumeAnalysisSummaryResponse(
            resume.LatestAnalysis.Id,
            resume.LatestAnalysis.Mode,
            resume.LatestAnalysis.Status,
            resume.LatestAnalysis.CreatedAt));
}
