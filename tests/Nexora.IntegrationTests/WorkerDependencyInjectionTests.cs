using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business;
using Nexora.Business.Practice;
using Nexora.Business.Privacy;
using Nexora.Data;
using Nexora.Data.Identity;
using Nexora.Integrations;

namespace Nexora.IntegrationTests;

public sealed class WorkerDependencyInjectionTests
{
    [Fact]
    public void WorkerCompositionResolvesAllPollingProcessorsWithoutIdentity()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=localhost;Database=nexora_worker_di_tests",
            ["Features:Ai"] = "false",
            ["Billing:Payment:Provider"] = "fake",
            ["Storage:Provider"] = "local",
            ["Storage:Local:RootPath"] = Path.Combine(Path.GetTempPath(), "nexora-worker-di-tests")
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        // Keep the processor composition identical to Nexora.Worker/Program.cs.
        services.AddBusiness();
        services.AddDataPersistence(configuration);
        services.AddIntegrations(configuration);

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        using var scope = provider.CreateScope();
        Assert.Null(scope.ServiceProvider.GetService<UserManager<ApplicationUser>>());
        Assert.Null(scope.ServiceProvider.GetService<IExternalAccountDeletionService>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPrivacyJobProcessor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IPracticeJobProcessor>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IScenarioStarJobProcessor>());
    }
}
