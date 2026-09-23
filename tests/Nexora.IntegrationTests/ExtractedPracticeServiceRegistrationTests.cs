using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Practice;
using Nexora.Data.Practice;
using Nexora.Data.Progress;

namespace Nexora.IntegrationTests;

public sealed class ExtractedPracticeServiceRegistrationTests
{
    [Fact]
    public void PracticeServicesResolveToDistinctScopedOwners()
    {
        using var factory = new NexoraApiFactory();
        using var scope = factory.Services.CreateScope();

        Assert.IsType<ScenarioService>(scope.ServiceProvider.GetRequiredService<IScenarioService>());
        Assert.IsType<StarAttemptService>(scope.ServiceProvider.GetRequiredService<IStarAttemptService>());
        Assert.IsType<ProgressService>(scope.ServiceProvider.GetRequiredService<IProgressService>());
        Assert.IsType<ScenarioStarJobProcessor>(scope.ServiceProvider.GetRequiredService<IScenarioStarJobProcessor>());
    }
}
