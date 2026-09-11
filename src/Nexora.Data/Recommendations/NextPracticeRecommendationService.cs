using Nexora.Business.Learning;
using Nexora.Business.Recommendations;
using Nexora.Business.Skills;

namespace Nexora.Data.Recommendations;

public sealed class NextPracticeRecommendationService(
    ILearningPathService learningPathService,
    ISkillProfileService skillProfileService) : INextPracticeRecommendationService
{
    public async Task<NextPracticeRecommendationView?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var learningPath = await learningPathService.GetAsync(userId, cancellationToken);
        var skillProfile = await skillProfileService.GetAsync(userId, cancellationToken);
        return NextPracticeRecommendationPolicy.Select(learningPath, skillProfile);
    }
}
