using Microsoft.Extensions.Configuration;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
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

    [Fact]
    public void GeminiProviderRequiresSingleAdapterAttemptBecauseExecutorOwnsRetryBudget()
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddIntegrations(Configuration("gemini", geminiMaxAttempts: 2))
            .BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<GeminiOptions>>().Value);

        Assert.Contains("exactly 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficialDeepSeekBaseUrlIsAcceptedWhenAiIsEnabled()
    {
        using var services = BuildDeepSeekServices("https://api.deepseek.com");

        var options = services.GetRequiredService<IOptions<DeepSeekOptions>>().Value;

        Assert.Equal("https://api.deepseek.com", options.BaseUrl);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/unexpected")]
    [InlineData("https://api.deepseek.com?query=1")]
    public void InvalidDeepSeekBaseUrlIsRejectedWhenAiIsEnabled(string baseUrl)
    {
        using var services = BuildDeepSeekServices(baseUrl);

        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<DeepSeekOptions>>().Value);
    }

    private static ServiceProvider BuildServices(string provider = "gemini") =>
        new ServiceCollection()
            .AddLogging()
            .AddIntegrations(Configuration(provider))
            .BuildServiceProvider();

    private static ServiceProvider BuildDeepSeekServices(string baseUrl) =>
        new ServiceCollection()
            .AddLogging()
            .AddIntegrations(Configuration("deepseek", aiEnabled: true, deepSeekBaseUrl: baseUrl))
            .BuildServiceProvider();

    private static IConfiguration Configuration(
        string provider,
        bool aiEnabled = false,
        string? deepSeekBaseUrl = null,
        int geminiMaxAttempts = 1) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:Ai"] = aiEnabled ? "true" : "false",
                ["Ai:Provider"] = provider,
                ["Ai:Gemini:ApiKey"] = "test-gemini-key",
                ["Ai:Gemini:Model"] = "test-gemini-model",
                ["Ai:Gemini:MaxAttempts"] = geminiMaxAttempts.ToString(CultureInfo.InvariantCulture),
                ["Ai:DeepSeek:ApiKey"] = "test-deepseek-key",
                ["Ai:DeepSeek:BaseUrl"] = deepSeekBaseUrl ?? "https://api.deepseek.com",
                ["Ai:DeepSeek:Model"] = "deepseek-v4-flash",
                ["Storage:Local:RootPath"] = ".nexora-test-storage"
            })
            .Build();
}
