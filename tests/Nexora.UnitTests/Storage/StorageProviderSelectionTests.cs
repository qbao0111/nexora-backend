using System.Globalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Business.Practice;
using Nexora.Business.Storage;
using Nexora.Integrations;
using Nexora.Integrations.Storage;

namespace Nexora.UnitTests.Storage;

public sealed class StorageProviderSelectionTests
{
    [Fact]
    public void LocalProviderRemainsTheDefault()
    {
        var descriptors = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddIntegrations(Configuration());
        using var services = descriptors.BuildServiceProvider();

        Assert.IsType<LocalStorageProvider>(services.GetRequiredService<IStorageProvider>());
        var descriptor = descriptors.Last(item => item.ServiceType == typeof(IUploadProvider));
        Assert.Equal(typeof(LocalUploadProvider), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    [Fact]
    public void R2ProviderIsSelectedWhenConfigured()
    {
        using var services = BuildServices(Configuration("r2", includeLocalRoot: false));

        Assert.IsType<R2StorageProvider>(services.GetRequiredService<IStorageProvider>());
        var options = services.GetRequiredService<IOptions<R2StorageOptions>>().Value;
        Assert.Equal("private-bucket", options.Bucket);
    }

    [Fact]
    public void R2UploadProviderIsScopedAndSelectedWithR2Storage()
    {
        var services = new ServiceCollection().AddIntegrations(Configuration("r2", includeLocalRoot: false));
        var descriptor = services.Last(item => item.ServiceType == typeof(IUploadProvider));

        Assert.Equal(typeof(R2UploadProvider), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    [Fact]
    public void UnknownProviderFailsClosed()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddIntegrations(Configuration("unsupported")));
    }

    [Theory]
    [InlineData("AccountId", "AccountId is required")]
    [InlineData("Bucket", "Bucket is required")]
    [InlineData("AccessKeyId", "AccessKeyId is required")]
    [InlineData("SecretAccessKey", "SecretAccessKey is required")]
    [InlineData("Endpoint", "Endpoint must be an absolute HTTPS URL")]
    public void MissingR2ConfigurationFailsValidation(string key, string expectedMessage)
    {
        var configuration = Configuration("r2");
        var values = configuration.AsEnumerable().ToDictionary(pair => pair.Key, pair => pair.Value);
        values[$"Storage:R2:{key}"] = string.Empty;

        using var services = BuildServices(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        var exception = Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<R2StorageOptions>>().Value);
        Assert.Contains(expectedMessage, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://account-id.r2.cloudflarestorage.com")]
    [InlineData("not a uri")]
    [InlineData("https://account-id.r2.cloudflarestorage.com?signature=secret")]
    [InlineData(" https://account-id.r2.cloudflarestorage.com")]
    [InlineData("https://account-id.r2.cloudflarestorage.com ")]
    public void R2EndpointMustBeAbsoluteHttpsWithoutQuery(string endpoint)
    {
        using var services = BuildServices(Configuration("r2", endpoint));

        Assert.Throws<OptionsValidationException>(() =>
            services.GetRequiredService<IOptions<R2StorageOptions>>().Value);
    }

    private static ServiceProvider BuildServices(IConfiguration configuration) =>
        new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddIntegrations(configuration)
            .BuildServiceProvider();

    private static IConfiguration Configuration(
        string provider = "local",
        string endpoint = "https://account-id.r2.cloudflarestorage.com",
        bool includeLocalRoot = true) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Features:Ai"] = "false",
                ["Features:Payment"] = "false",
                ["Features:Upload"] = "true",
                ["Storage:Provider"] = provider,
                ["Storage:Local:RootPath"] = includeLocalRoot ? ".nexora-test-storage" : null,
                ["Storage:R2:AccountId"] = "account-id",
                ["Storage:R2:Bucket"] = "private-bucket",
                ["Storage:R2:AccessKeyId"] = "test-access-key",
                ["Storage:R2:SecretAccessKey"] = "test-secret-key",
                ["Storage:R2:Endpoint"] = endpoint,
                ["Ai:Provider"] = "gemini",
                ["Ai:Gemini:ApiKey"] = "test-gemini-key",
                ["Ai:Gemini:Model"] = "test-gemini-model",
                ["Ai:Gemini:MaxAttempts"] = 1.ToString(CultureInfo.InvariantCulture)
            })
            .Build();
}
