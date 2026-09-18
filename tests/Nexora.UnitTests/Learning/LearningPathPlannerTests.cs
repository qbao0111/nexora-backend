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
        Assert.Equal("Khắc phục các điểm yếu quan trọng", LearningPathRules.MilestoneTitle(LearningPathValues.CriticalMilestone));
        Assert.Equal("Phát triển các kỹ năng cần cải thiện", LearningPathRules.MilestoneTitle(LearningPathValues.DevelopingMilestone));
        Assert.Equal("Củng cố năng lực và minh chứng", LearningPathRules.MilestoneTitle(LearningPathValues.SupportingMilestone));
        Assert.Equal(
            [LearningPathValues.CriticalMilestone, LearningPathValues.DevelopingMilestone],
            plan.Milestones.Select(item => item.Code));
    }

    [Fact]
    public void ActivityCopyIsVietnameseAndKeepsActivityIdentityAndTechnicalNames()
    {
        var scenarioId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var profile = new SkillProfileView(
            [
                Competency("scenario.customer_service", "Customer Service", "scenario", 40),
                Competency("scenario.prioritization", "Prioritization", "scenario", 60),
                Competency("behavioral.situation", "Situation", "behavioral", 61),
                Competency("interview.risk_management", "Risk Management", "interview", 74),
                Competency("resume.impact_achievements", "Impact Achievements", "resume", 50),
                Competency("scenario.react", "React", "scenario", 40),
                Competency("interview.asp_net_core", "ASP.NET Core", "interview", 40),
                Competency("resume.docker", "Docker", "resume", 40)
            ],
            []);

        var plan = LearningPathPlanner.Create(profile, [new LearningPathScenarioResource(scenarioId, "Customer Service")]);
        var byCode = plan.Activities.ToDictionary(item => item.CompetencyCode!, StringComparer.Ordinal);

        Assert.Equal("scenario:scenario.customer_service:10000000000000000000000000000001", byCode["scenario.customer_service"].Key);
        Assert.Equal(LearningPathValues.Scenario, byCode["scenario.customer_service"].Type);
        Assert.Equal("Luyện tập dịch vụ khách hàng", byCode["scenario.customer_service"].Title);
        Assert.Equal("Thực hiện một phiên luyện tập tập trung để cải thiện dịch vụ khách hàng.", byCode["scenario.customer_service"].Description);
        Assert.Equal(LearningPathValues.CriticalMilestone, byCode["scenario.customer_service"].MilestoneCode);
        Assert.Equal(1, byCode["scenario.customer_service"].Priority);
        Assert.Equal("external_learning:scenario.prioritization", byCode["scenario.prioritization"].Key);
        Assert.Equal(LearningPathValues.ExternalLearning, byCode["scenario.prioritization"].Type);
        Assert.Equal("Học và luyện tập khả năng sắp xếp thứ tự ưu tiên", byCode["scenario.prioritization"].Title);
        Assert.Equal("Học hoặc luyện tập khả năng sắp xếp thứ tự ưu tiên bằng một tài nguyên phù hợp.", byCode["scenario.prioritization"].Description);
        Assert.Equal("star_drill:behavioral.situation", byCode["behavioral.situation"].Key);
        Assert.Equal(LearningPathValues.StarDrill, byCode["behavioral.situation"].Type);
        Assert.Equal("Luyện tình huống theo phương pháp STAR", byCode["behavioral.situation"].Title);
        Assert.Equal("Thực hành trình bày tình huống trong câu trả lời theo phương pháp STAR.", byCode["behavioral.situation"].Description);
        Assert.Equal("behavioral.situation", byCode["behavioral.situation"].CompetencyCode);
        Assert.Equal(2, byCode["behavioral.situation"].Priority);
        Assert.Equal(LearningPathValues.DevelopingMilestone, byCode["behavioral.situation"].MilestoneCode);
        Assert.Equal("interview:interview.risk_management", byCode["interview.risk_management"].Key);
        Assert.Equal(LearningPathValues.Interview, byCode["interview.risk_management"].Type);
        Assert.Equal("Luyện quản lý rủi ro trong phỏng vấn", byCode["interview.risk_management"].Title);
        Assert.Equal("Thực hiện một phiên phỏng vấn tập trung để cải thiện quản lý rủi ro.", byCode["interview.risk_management"].Description);
        Assert.Equal("interview.risk_management", byCode["interview.risk_management"].CompetencyCode);
        Assert.Equal(2, byCode["interview.risk_management"].Priority);
        Assert.Equal(LearningPathValues.DevelopingMilestone, byCode["interview.risk_management"].MilestoneCode);
        Assert.Equal("resume_improvement:resume.impact_achievements", byCode["resume.impact_achievements"].Key);
        Assert.Equal(LearningPathValues.ResumeImprovement, byCode["resume.impact_achievements"].Type);
        Assert.Equal("Cải thiện thành tích tạo ra tác động trong CV", byCode["resume.impact_achievements"].Title);
        Assert.Equal("Thực hiện một phiên luyện tập tập trung để cải thiện thành tích tạo ra tác động.", byCode["resume.impact_achievements"].Description);
        Assert.Equal("resume.impact_achievements", byCode["resume.impact_achievements"].CompetencyCode);
        Assert.Equal(1, byCode["resume.impact_achievements"].Priority);
        Assert.Equal(LearningPathValues.CriticalMilestone, byCode["resume.impact_achievements"].MilestoneCode);
        Assert.Equal("external_learning:scenario.react", byCode["scenario.react"].Key);
        Assert.Equal("Học và luyện tập React", byCode["scenario.react"].Title);
        Assert.Equal("interview:interview.asp_net_core", byCode["interview.asp_net_core"].Key);
        Assert.Equal("Luyện ASP.NET Core trong phỏng vấn", byCode["interview.asp_net_core"].Title);
        Assert.Equal("resume_improvement:resume.docker", byCode["resume.docker"].Key);
        Assert.Equal("Cải thiện Docker trong CV", byCode["resume.docker"].Title);
        Assert.Equal(scenarioId, byCode["scenario.customer_service"].ResourceId);
        Assert.Null(byCode["scenario.prioritization"].ResourceId);
        Assert.Null(byCode["scenario.prioritization"].ExternalUrl);
        Assert.Equal("scenario.customer_service", byCode["scenario.customer_service"].CompetencyCode);
        Assert.Equal(2, byCode["scenario.prioritization"].Priority);
        Assert.Equal(LearningPathValues.DevelopingMilestone, byCode["scenario.prioritization"].MilestoneCode);
        Assert.All(plan.Activities, item => Assert.True(
            item.Description.StartsWith("Thực hiện", StringComparison.Ordinal) ||
            item.Description.StartsWith("Thực hành", StringComparison.Ordinal) ||
            item.Description.StartsWith("Học hoặc luyện tập", StringComparison.Ordinal)));

        var userCopy = string.Join('\n', plan.Milestones.Select(item => item.Title)
            .Concat(plan.Activities.SelectMany(item => new[] { item.Title, item.Description })));
        foreach (var obsoleteTemplate in new[]
        {
            "Fix critical gaps", "Develop emerging skills", "Strengthen supporting evidence", "Practice Customer Service",
            "Drill Situation with STAR", "Practice Risk Management in an interview", "Study Prioritization with guided practice",
            "Improve Impact Achievements in your CV", "Use a focused practice session to improve", "Resume improvement:",
            "Address this CV signal:"
        })
            Assert.DoesNotContain(obsoleteTemplate, userCopy, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("behavioral.task", "Task", "nhiệm vụ")]
    [InlineData("behavioral.action", "Action", "hành động")]
    [InlineData("behavioral.result", "Result", "kết quả")]
    [InlineData("interview.situation", "Situation", "tình huống")]
    [InlineData("resume.impact_evidence", "Impact Evidence", "minh chứng về tác động")]
    [InlineData("resume.project_evidence", "Project Evidence", "minh chứng dự án")]
    public void KnownCanonicalLabelsUseLearningPathVietnameseDisplayCopy(string code, string name, string expected)
    {
        Assert.Equal(expected, LearningPathDisplayNames.ForCompetency(code, name));
        Assert.Equal(expected, LearningPathDisplayNames.ForQualitativeLabel(name));
    }

    [Fact]
    public void QualitativeCopyDoesNotChangeItsCanonicalOrLegacyIdentity()
    {
        var label = "Impact Evidence";
        var topicIdentity = LearningPathQualitativeSignalDeduper.StableTopicIdentityForLabel(label);
        var plan = LearningPathPlanner.Create(
            new SkillProfileView([], [Weakness(label, DateTimeOffset.UnixEpoch)]),
            []);
        var activity = Assert.Single(plan.Activities);

        Assert.Equal(LearningPathRules.QualitativeActivityKeyForTopicIdentity(topicIdentity), activity.Key);
        Assert.Equal(LearningPathRules.LegacyQualitativeActivityKey(label), activity.LegacyQualitativeActivityKey);
        Assert.Equal("Cải thiện CV: minh chứng về tác động", activity.Title);
        Assert.Equal("Cải thiện điểm cần chú ý trong CV: minh chứng về tác động.", activity.Description);
        Assert.Equal(LearningPathValues.ResumeImprovement, activity.Type);
        Assert.Null(activity.CompetencyCode);
        Assert.Equal(LearningPathValues.SupportingMilestone, activity.MilestoneCode);
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
        Assert.Equal("docker/kubernetes", signal.Signal.Label);
        Assert.Equal(newer, signal.Signal.LatestEvidenceAt);
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
        Assert.Equal(7, signals.Select(item => item.Signal.Label).Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(
            QualitativeActivityKey("Thiếu kinh nghiệm"),
            QualitativeActivityKey("Need skills and knowledge"));
        Assert.Equal(
            QualitativeActivityKey("Thiếu kinh nghiệm"),
            QualitativeActivityKey("KINH NGHIỆM THIẾU"));
    }

    [Theory]
    [InlineData(" Docker / Kubernetes ", "docker/kubernetes")]
    [InlineData("Thiếu kinh nghiệm với Docker và Kubernetes", "Chưa thể hiện kinh nghiệm triển khai Docker/Kubernetes")]
    public void EquivalentQualitativeTopicsKeepTheSameKeyAcrossSeparateGenerations(string firstLabel, string laterLabel)
    {
        var first = QualitativeActivityKey(firstLabel);
        var later = QualitativeActivityKey(laterLabel);

        Assert.Equal(first, later);
    }

    [Fact]
    public void QualitativeCanonicalIdentityKeepsDistinctTopicsAndLanguagesSeparate()
    {
        var keys = new[]
        {
            QualitativeActivityKey("Missing Docker and Kubernetes experience"),
            QualitativeActivityKey("Missing SQL query optimization experience"),
            QualitativeActivityKey("Need to improve React state management"),
            QualitativeActivityKey("Missing C# API development experience"),
            QualitativeActivityKey("Missing C++ API development experience")
        };

        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.NotEqual(
            QualitativeActivityKey("Missing C# API development experience"),
            QualitativeActivityKey("Missing C++ API development experience"));
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

    private static string QualitativeActivityKey(string label) =>
        Assert.Single(LearningPathPlanner.Create(
            new SkillProfileView([], [Weakness(label, DateTimeOffset.UnixEpoch)]),
            []).Activities).Key;

    private static SkillProfileWeaknessSignal Weakness(string label, DateTimeOffset at) =>
        new(SkillProfileSourceTypes.ResumeAnalysis, label, at);
}
