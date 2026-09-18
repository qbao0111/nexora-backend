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
    public void QualitativeSignalsCollapseExactNormalizedDuplicatesAndKeepNewestEvidence()
    {
        var older = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(1);

        var deduplicated = LearningPathQualitativeSignalDeduper.Deduplicate([
            Weakness(" Docker / Kubernetes ", older),
            Weakness("docker/kubernetes", newer)
        ]);

        var signal = Assert.Single(deduplicated);
        Assert.Equal("docker/kubernetes", signal.Label);
        Assert.Equal(newer, signal.LatestEvidenceAt);
    }

    [Fact]
    public void QualitativeSignalsCollapseHighlyOverlappingVietnameseTopicLabels()
    {
        var older = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(1);
        var profile = new SkillProfileView([], [
            Weakness("Thiếu kinh nghiệm với Docker và Kubernetes", older),
            Weakness("Chưa thể hiện kinh nghiệm triển khai Docker/Kubernetes", newer)
        ]);

        var plan = LearningPathPlanner.Create(profile, []);
        var activity = Assert.Single(plan.Activities);

        Assert.Null(activity.CompetencyCode);
        Assert.Equal(newer, activity.LatestEvidenceAt);
        Assert.Contains("Docker và Kubernetes", activity.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void QualitativeDedupeKeepsDifferentTopicsAndDoesNotMergeGenericBoilerplate()
    {
        var at = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var signals = LearningPathQualitativeSignalDeduper.Deduplicate([
            Weakness("Missing Docker and Kubernetes experience", at),
            Weakness("Thiếu kinh nghiệm tối ưu SQL query", at),
            Weakness("Need to improve React state management", at),
            Weakness("Missing C# API development experience", at),
            Weakness("Missing C++ API development experience", at),
            Weakness("Thiếu kinh nghiệm", at),
            Weakness("Need skills and knowledge", at)
        ]);

        Assert.Equal(7, signals.Count);
        Assert.Equal(7, signals.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EquivalentQualitativePlanningKeepsStableActivityKeysRegardlessOfInputOrder()
    {
        var at = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var signals = new[]
        {
            Weakness("Thiếu kinh nghiệm với Docker và Kubernetes", at),
            Weakness("Chưa thể hiện kinh nghiệm triển khai Docker/Kubernetes", at.AddHours(1))
        };
        var first = LearningPathPlanner.Create(new SkillProfileView([], signals), []);
        var repeated = LearningPathPlanner.Create(new SkillProfileView([], signals.Reverse().ToArray()), []);

        Assert.Equal(Assert.Single(first.Activities).Key, Assert.Single(repeated.Activities).Key);
        Assert.Equal(first.Activities, repeated.Activities);
    }

    [Fact]
    public void PlannerKeepsAtMostFourQualitativeActivitiesAndPrefersRecentEvidence()
    {
        var firstEvidenceAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var signals = Enumerable.Range(0, 7)
            .Select(index => Weakness($"Missing topic-{index:D2} experience", firstEvidenceAt.AddDays(index)))
            .ToArray();

        var plan = LearningPathPlanner.Create(new SkillProfileView([], signals), []);
        var qualitative = plan.Activities.Where(item => item.CompetencyCode is null).ToArray();

        Assert.Equal(4, LearningPathRules.MaximumQualitativeActivities);
        Assert.Equal(4, qualitative.Length);
        Assert.Equal(
            [firstEvidenceAt.AddDays(3), firstEvidenceAt.AddDays(4), firstEvidenceAt.AddDays(5), firstEvidenceAt.AddDays(6)],
            qualitative.Select(item => item.LatestEvidenceAt).OrderBy(item => item));
    }

    [Fact]
    public void PlannerKeepsAtMostTenActivitiesAndNeverLetsQualitativeSignalsCrowdOutCriticalGaps()
    {
        var competencies = Enumerable.Range(0, 8)
            .Select(index => Competency($"interview.critical_{index:D2}", $"Critical {index:D2}", "interview", 10 + index))
            .Concat(Enumerable.Range(0, 5)
                .Select(index => Competency($"interview.developing_{index:D2}", $"Developing {index:D2}", "interview", 60 + index)))
            .ToArray();
        var at = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var signals = Enumerable.Range(0, 4)
            .Select(index => Weakness($"Missing supporting topic-{index:D2}", at.AddDays(index)))
            .ToArray();

        var plan = LearningPathPlanner.Create(new SkillProfileView(competencies, signals), []);

        Assert.Equal(10, LearningPathRules.MaximumCurrentActivities);
        Assert.Equal(10, plan.Activities.Count);
        Assert.Equal(8, plan.Activities.Count(item => item.Priority == 1));
        Assert.Equal(2, plan.Activities.Count(item => item.Priority == 2));
        Assert.DoesNotContain(plan.Activities, item => item.CompetencyCode is null);
    }

    [Fact]
    public void NumericCompetencyAndQualitativeResumeSignalRemainSeparateTopics()
    {
        var at = new DateTimeOffset(2026, 9, 10, 8, 0, 0, TimeSpan.Zero);
        var profile = new SkillProfileView(
            [Competency("resume.docker", "Docker", "resume", 40)],
            [Weakness("Missing Docker experience", at)]);

        var plan = LearningPathPlanner.Create(profile, []);

        Assert.Equal(2, plan.Activities.Count);
        Assert.Contains(plan.Activities, item => item.CompetencyCode == "resume.docker");
        Assert.Contains(plan.Activities, item => item.CompetencyCode is null);
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

    private static SkillProfileWeaknessSignal Weakness(string label, DateTimeOffset at) =>
        new(SkillProfileSourceTypes.ResumeAnalysis, label, at);
}
