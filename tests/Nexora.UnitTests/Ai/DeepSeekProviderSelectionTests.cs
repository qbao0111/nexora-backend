using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Ai;
using Nexora.Business.Practice;
using Nexora.Integrations;
using Nexora.Integrations.Ai;

namespace Nexora.UnitTests.Ai;

public sealed class DeepSeekProviderSelectionTests
{
    [Fact]
    public void GeminiIsTheDefaultTextProviderAndOcrRemainsGemini()
    {
        using var services = BuildServices();

        Assert.IsType<GeminiAiProvider>(services.GetRequiredService<IAiProvider>());
        Assert.IsType<GeminiDocumentOcrProvider>(services.GetRequiredService<IDocumentOcrProvider>());
    }

    [Fact]
    public void DeepSeekSelectionResolvesDeepSeekTextProviderAndGeminiOcr()
    {
        using var services = BuildServices("deepseek");

        Assert.IsType<DeepSeekAiProvider>(services.GetRequiredService<IAiProvider>());
        Assert.IsType<GeminiDocumentOcrProvider>(services.GetRequiredService<IDocumentOcrProvider>());
    }

    [Fact]
    public void UnknownProviderFailsClosed()
    {
        var configuration = Configuration("unsupported");
        var serviceCollection = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => serviceCollection.AddIntegrations(configuration));
    }

    [Fact]
    public void SameSelectorIsUsedByApiAndWorkerCompositionRoots()
    {
        using var apiServices = BuildServices("deepseek");
        using var workerServices = BuildServices("deepseek");

        Assert.IsType<DeepSeekAiProvider>(apiServices.GetRequiredService<IAiProvider>());
        Assert.IsType<DeepSeekAiProvider>(workerServices.GetRequiredService<IAiProvider>());
    }

    private static ServiceProvider BuildServices(string provider = "gemini") =>
        new ServiceCollection()
            .AddLogging()
            .AddIntegrations(Configuration(provider))
            .BuildServiceProvider();

    private static IConfiguration Configuration(string provider) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:Ai"] = "false",
                ["Ai:Provider"] = provider,
                ["Storage:Local:RootPath"] = ".nexora-test-storage"
            })
            .Build();
}
