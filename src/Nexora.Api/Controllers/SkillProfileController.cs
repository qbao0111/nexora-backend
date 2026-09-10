using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexora.Api.Contracts;
using Nexora.Api.Infrastructure;
using Nexora.Business.Skills;

namespace Nexora.Api.Controllers;

[ApiController, Authorize, Route("api/v1/skill-profile")]
public sealed class SkillProfileController(ISkillProfileService skillProfileService) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ApiResponse<SkillProfileResponse>>> Get(CancellationToken cancellationToken)
    {
        var profile = await skillProfileService.GetAsync(User.GetRequiredUserId(), cancellationToken);
        return Ok(new ApiResponse<SkillProfileResponse>(new SkillProfileResponse(
            profile.Competencies.Select(competency => new SkillProfileCompetencyResponse(
                competency.Code,
                competency.Name,
                competency.Category,
                competency.Score,
                competency.EvidenceCount,
                competency.LatestEvidenceAt,
                competency.Sources.Select(source => new SkillProfileSourceResponse(
                    source.SourceType,
                    source.EvidenceCount,
                    source.LatestEvidenceAt)).ToArray())).ToArray(),
            profile.WeaknessSignals.Select(signal => new SkillProfileWeaknessSignalResponse(
                signal.SourceType,
                signal.Label,
                signal.LatestEvidenceAt)).ToArray())));
    }
}
