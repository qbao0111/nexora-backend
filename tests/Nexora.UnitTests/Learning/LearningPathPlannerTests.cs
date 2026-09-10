using Nexora.Business.Learning;
using Nexora.Business.Skills;

namespace Nexora.UnitTests.Learning;

public sealed class LearningPathPlannerTests
{
    [Fact]
    public void NumericGapsUseDeterministicThresholdsAndActivityTypes()
    {
        var profile = new SkillProfileView(
            [
                Competency("scenario.problem_solving", "Problem Solving", "scenario", 59),
                Competency("interview.communication", "Communication", "interview", 60),
                Competency("behavioral.action", "Action", "behavioral", 74),
                Competency("resume.clarity", "Clarity", "resume", 75)
            ],
            []);

        var plan = LearningPathPlanner.Create(profile, [new LearningPathScenarioResource(Guid.Parse("10000000-0000-0000-0000-000000000001"), "Problem Solving")]);

        Assert.Equal(
            [LearningPathValues.Scenario, LearningPathValues.Interview, LearningPathValues.StarDrill],
            plan.Activities.Select(item => item.Type));
        Assert.Equal([1, 2, 2], plan.Activities.Select(item => item.Priority));
        Assert.DoesNotContain(plan.Activities, item => item.CompetencyCode == "resume.clarity");
        Assert.Equal(LearningPathValues.CriticalMilestone, plan.Activities.First().MilestoneCode);
        Assert.Equal(LearningPathValues.DevelopingMilestone, plan.Activities.Skip(1).First().MilestoneCode);
    }

    [Fact]
    public void ScenarioGapsWithoutPublishedResourcesBecomeExternalLearningActivities()
    {
        var profile = new SkillProfileView([Competency("scenario.incident_response", "Incident Response", "scenario", 40)], []);

        var plan = LearningPathPlanner.Create(profile, []);
        var repeated = LearningPathPlanner.Create(profile, []);
        var activity = Assert.Single(plan.Activities);

        Assert.Equal(LearningPathValues.ExternalLearning, activity.Type);
        Assert.Equal("scenario.incident_response", activity.CompetencyCode);
        Assert.Null(activity.ResourceId);
        Assert.Null(activity.ExternalUrl);
        Assert.Equal(1, activity.Priority);
        Assert.Equal(LearningPathValues.CriticalMilestone, activity.MilestoneCode);
        Assert.Equal(plan.Activities, repeated.Activities);
        Assert.Equal(plan.Milestones, repeated.Milestones);
    }

    [Fact]
    public void QualitativeCvSignalsCreateSupportingResumeActivitiesWithoutScores()
    {
        var at = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);
        var profile = new SkillProfileView(
            [Competency("resume.clarity", "Clarity", "resume", 40)],
            [
                new SkillProfileWeaknessSignal("cv_analysis", " SQL ", at),
                new SkillProfileWeaknessSignal("interview", "Do not turn this into an activity.", at)
            ]);

        var plan = LearningPathPlanner.Create(profile, []);

        var qualitative = Assert.Single(plan.Activities, item => item.CompetencyCode is null);
        Assert.Equal(LearningPathValues.ResumeImprovement, qualitative.Type);
        Assert.Equal(3, qualitative.Priority);
        Assert.Equal(LearningPathValues.SupportingMilestone, qualitative.MilestoneCode);
        Assert.Null(qualitative.ResourceId);
        Assert.Null(qualitative.ExternalUrl);
        Assert.Equal("resume.clarity", Assert.Single(plan.Activities, item => item.CompetencyCode is not null).CompetencyCode);
    }

    [Fact]
    public void RepeatedPlanningHasStableKeysOrderAndMilestones()
    {
        var profile = new SkillProfileView(
            [
                Competency("interview.zed", "Zed", "interview", 70),
                Competency("interview.alpha", "Alpha", "interview", 70),
                Competency("behavioral.result", "Result", "behavioral", 30)
            ],
            []);
        var resources = new[] { new LearningPathScenarioResource(Guid.NewGuid(), "Unused") };

        var first = LearningPathPlanner.Create(profile, resources);
        var second = LearningPathPlanner.Create(profile, resources);

        Assert.Equal(first.Milestones, second.Milestones);
        Assert.Equal(first.Activities, second.Activities);
        Assert.Equal(first.Activities.Select(item => item.Key).Distinct(), first.Activities.Select(item => item.Key));
        Assert.Equal([0, 1], first.Activities.Where(item => item.MilestoneCode == LearningPathValues.DevelopingMilestone).Select(item => item.SortOrder));
    }

    [Fact]
    public void ScenarioResourceSelectionUsesTheLowestGuidForDuplicateCompetencies()
    {
        var firstId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("10000000-0000-0000-0000-000000000002");
        var profile = new SkillProfileView([Competency("scenario.problem_solving", "Problem Solving", "scenario", 50)], []);

        var plan = LearningPathPlanner.Create(profile, [
            new LearningPathScenarioResource(secondId, "problem-solving"),
            new LearningPathScenarioResource(firstId, "Problem Solving")
        ]);

        Assert.Equal(firstId, Assert.Single(plan.Activities).ResourceId);
    }

    [Fact]
    public void LearningCycleActivityKeyIsStableAndBounded()
    {
        var baseKey = "resume_improvement:resume.clarity";
        var evidenceAt = new DateTimeOffset(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);

        var first = LearningPathRules.LearningCycleActivityKey(baseKey, evidenceAt);
        var second = LearningPathRules.LearningCycleActivityKey(baseKey, evidenceAt);

        Assert.Equal(first, second);
        Assert.NotEqual(first, LearningPathRules.LearningCycleActivityKey(baseKey, evidenceAt.AddMinutes(1)));
        Assert.True(first.Length <= LearningPathRules.ActivityKeyMaxLength);
    }

    private static SkillProfileCompetency Competency(string code, string name, string category, int score) =>
        new(code, name, category, score, 1, DateTimeOffset.UtcNow, []);
}
