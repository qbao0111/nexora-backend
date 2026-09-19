using System.Text;
using System.Text.Json;

namespace Nexora.UnitTests.Scenarios;

public sealed class ScenarioLibraryDatasetTests
{
    private static readonly Dictionary<string, int> ExpectedCategoryCounts = new(StringComparer.Ordinal)
    {
        ["banking"] = 4,
        ["ecommerce"] = 4,
        ["logistics"] = 4,
        ["backend"] = 5,
        ["frontend"] = 5,
        ["dotnet"] = 5,
        ["java"] = 5,
        ["database"] = 5,
        ["devops"] = 5,
        ["qa-testing"] = 5,
        ["mobile"] = 5,
        ["software-architecture"] = 5,
        ["app-security"] = 5
    };

    private static readonly Dictionary<string, int> ExpectedDifficultyCounts = new(StringComparer.Ordinal)
    {
        ["easy"] = 13,
        ["medium"] = 36,
        ["hard"] = 13
    };

    private static readonly IReadOnlySet<string> LegacyScenarioSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "banking-disputed-transfer",
        "banking-suspicious-transaction",
        "banking-kpi-vs-compliance",
        "banking-branch-service-outage",
        "ecommerce-refund-demand",
        "ecommerce-flash-sale-overload",
        "ecommerce-seller-policy-violation",
        "ecommerce-out-of-stock-after-payment",
        "logistics-driver-breakdown",
        "logistics-warehouse-stock-mismatch",
        "logistics-weather-delivery-disruption",
        "logistics-repeated-late-deliveries"
    };

    private static readonly HashSet<string> SoftwareEngineeringScenarioSlugs = new(StringComparer.Ordinal)
    {
        "backend-api-validation-production",
        "backend-duplicate-order-retry",
        "backend-third-party-api-timeout",
        "backend-cache-stale-permission",
        "backend-event-processing-double-consume",
        "frontend-form-state-reset",
        "frontend-stale-react-query-data",
        "frontend-race-search-results",
        "frontend-hydration-production-only",
        "frontend-large-dashboard-performance",
        "dotnet-async-blocking-request",
        "dotnet-efcore-nplusone",
        "dotnet-dbcontext-concurrency",
        "dotnet-migration-production-risk",
        "dotnet-auth-policy-data-leak",
        "java-spring-validation-mismatch",
        "java-jpa-lazy-loading",
        "java-transaction-partial-write",
        "java-threadpool-exhaustion",
        "java-distributed-transaction-boundary",
        "database-missing-index-search",
        "database-deadlock-orders",
        "database-soft-delete-unique-key",
        "database-zero-downtime-schema-change",
        "database-inventory-concurrency",
        "devops-env-production-missing",
        "devops-docker-works-local-not-container",
        "devops-ci-flaky-tests",
        "devops-deployment-healthcheck-loop",
        "devops-production-incident-release",
        "qa-repro-intermittent-bug",
        "qa-regression-release-deadline",
        "qa-e2e-flaky-selector",
        "qa-api-contract-mismatch",
        "qa-critical-payment-test-gap",
        "mobile-api-offline-error",
        "mobile-token-refresh-race",
        "mobile-background-state-loss",
        "mobile-api-version-old-client",
        "mobile-release-crash-specific-device",
        "software-architecture-service-boundary",
        "software-architecture-monolith-scaling",
        "software-architecture-sync-vs-async",
        "software-architecture-api-versioning",
        "software-architecture-microservice-data-consistency",
        "app-security-secret-committed",
        "app-security-idor-resource-access",
        "app-security-xss-user-content",
        "app-security-jwt-long-lived",
        "app-security-account-takeover-response"
    };

    [Fact]
    public void DatasetHasExpectedCategoriesDifficultyAndSafeVietnameseContent()
    {
        var path = FindRepositoryFile("scripts/data/scenarios.vi.json");
        var bytes = File.ReadAllBytes(path);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var rawJson = utf8.GetString(bytes);

        using var document = JsonDocument.Parse(rawJson);
        var scenarios = document.RootElement.GetProperty("scenarios").EnumerateArray().ToArray();
        Assert.Equal(ExpectedCategoryCounts.Values.Sum(), scenarios.Length);

        var slugs = scenarios.Select(item => item.GetProperty("slug").GetString()!).ToArray();
        Assert.Equal(slugs.Length, slugs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(LegacyScenarioSlugs, slug => Assert.Contains(slug, slugs, StringComparer.Ordinal));

        var categoryCounts = scenarios.GroupBy(item => item.GetProperty("category").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Assert.Equal(ExpectedCategoryCounts.Count, categoryCounts.Count);
        foreach (var expected in ExpectedCategoryCounts)
            Assert.Equal(expected.Value, categoryCounts[expected.Key]);

        var difficultyCounts = scenarios.GroupBy(item => item.GetProperty("difficulty").GetString()!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        foreach (var expected in ExpectedDifficultyCounts)
            Assert.Equal(expected.Value, difficultyCounts[expected.Key]);

        var softwareEngineering = scenarios.Where(item => ExpectedCategoryCounts[item.GetProperty("category").GetString()!].Equals(5)).ToArray();
        var softwareCategoryCounts = ExpectedCategoryCounts.Where(item => item.Value == 5).ToArray();
        Assert.Equal(softwareCategoryCounts.Sum(item => item.Value), softwareEngineering.Length);
        var softwareEngineeringSlugs = softwareEngineering.Select(item => item.GetProperty("slug").GetString()!).ToArray();
        Assert.Equal(SoftwareEngineeringScenarioSlugs.Count, softwareEngineeringSlugs.Length);
        Assert.True(SoftwareEngineeringScenarioSlugs.SetEquals(softwareEngineeringSlugs));
        foreach (var category in softwareCategoryCounts.Select(item => item.Key))
        {
            var byCategory = scenarios.Where(item => item.GetProperty("category").GetString() == category).ToArray();
            Assert.Equal(1, byCategory.Count(item => item.GetProperty("difficulty").GetString() == "easy"));
            Assert.Equal(3, byCategory.Count(item => item.GetProperty("difficulty").GetString() == "medium"));
            Assert.Equal(1, byCategory.Count(item => item.GetProperty("difficulty").GetString() == "hard"));
        }

        Assert.All(scenarios, scenario =>
        {
            var content = scenario.GetProperty("content").GetString()!;
            Assert.Contains("## Bối cảnh", content, StringComparison.Ordinal);
            Assert.Contains("## Dữ kiện", content, StringComparison.Ordinal);
            Assert.Contains("## Nhiệm vụ của bạn", content, StringComparison.Ordinal);
            Assert.DoesNotContain("## Đáp án", content, StringComparison.Ordinal);
            Assert.DoesNotContain("## Gợi ý trả lời", content, StringComparison.Ordinal);
            Assert.DoesNotContain("## Câu trả lời mẫu", content, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CategoryMigrationOnlyAddsCategoriesAndPreservesScenarioAttempts()
    {
        var migrationsDirectory = Path.GetDirectoryName(FindRepositoryFile("src/Nexora.Data/Persistence/Migrations/NexoraDbContextModelSnapshot.cs"))!;
        var migrationPath = Directory.EnumerateFiles(migrationsDirectory, "*_AddSoftwareEngineeringScenarioCategories.cs").Single();
        var migration = File.ReadAllText(migrationPath, Encoding.UTF8);
        var up = migration[..migration.IndexOf("protected override void Down", StringComparison.Ordinal)];

        Assert.Contains("InsertData", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteData", up, StringComparison.Ordinal);
        Assert.DoesNotContain("DropTable", up, StringComparison.Ordinal);
        Assert.DoesNotContain("scenario_attempts", up, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var roots = new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };
        foreach (var root in roots)
        {
            var directory = new DirectoryInfo(root);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }
}
