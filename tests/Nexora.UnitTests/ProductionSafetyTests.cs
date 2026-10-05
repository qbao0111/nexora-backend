using Microsoft.Extensions.Configuration;
using Nexora.Integrations;

namespace Nexora.UnitTests;

public sealed class ProductionSafetyTests
{
    [Theory]
    [InlineData("deepseek")]
    [InlineData(" DeepSeek ")]
    public void ProductionAllowsApprovedAi(string provider) =>
        ProductionSafety.ValidateAdapters(true, false, Approved with { AiProvider = provider });

    [Theory]
    [InlineData("gemini")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void ProductionRejectsUnapprovedEnabledAi(string? provider)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateAdapters(true, false, Approved with { AiProvider = provider }));
        Assert.Contains("Ai:Provider=deepseek", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("payos")]
    [InlineData(" PayOS ")]
    public void ProductionAllowsApprovedPayment(string provider) =>
        ProductionSafety.ValidateAdapters(true, false, Approved with { PaymentProvider = provider });

    [Theory]
    [InlineData("fake")]
    [InlineData("sepay")]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void ProductionRejectsUnapprovedEnabledPayment(string? provider)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateAdapters(true, false, Approved with { PaymentProvider = provider }));
        Assert.Contains("Billing:Payment:Provider=payos", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gemini", "fake")]
    [InlineData("unknown", "unknown")]
    [InlineData(null, null)]
    public void DisabledCapabilitiesDoNotBlockAdapterGuard(string? aiProvider, string? paymentProvider) =>
        ProductionSafety.ValidateAdapters(true, false, new(false, false, aiProvider, paymentProvider, "r2"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ProductionRequiresR2RegardlessOfUploadGate(bool uploadEnabled)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:Ai"] = "false",
            ["Features:Payment"] = "false",
            ["Features:Upload"] = uploadEnabled.ToString(),
            ["Storage:Provider"] = "r2"
        }).Build();
        var adapters = ProductionAdapterSelection.FromConfiguration(configuration);
        ProductionSafety.ValidateAdapters(true, false, adapters);
        foreach (var localVolume in new[] { false, true })
        {
            var error = Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateAdapters(true, false,
                adapters with { StorageProvider = "local", LocalPersistentVolumeConfigured = localVolume }));
            Assert.Contains("durable storage", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    [InlineData(null)]
    public void ProductionRejectsUnapprovedStorage(string? provider) =>
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateAdapters(true, false, Approved with { StorageProvider = provider }));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DevelopmentTestingAndStagingKeepTheirAdapterChoices(bool isStaging)
    {
        ProductionSafety.ValidateAdapters(false, isStaging, new(true, true, "gemini", "fake", "r2"));
        ProductionSafety.ValidateAdapters(false, isStaging, new(true, true, "deepseek", "sepay", "local", true));
        if (isStaging)
            Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateAdapters(false, true, new(false, false, null, null, "local")));
        else
            ProductionSafety.ValidateAdapters(false, false, new(true, true, "gemini", "fake", "local"));
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateAdapters(false, isStaging, Approved with { StorageProvider = "unknown" }));
    }

    [Fact]
    public void ConfigurationSnapshotNormalizesApprovedCombinedStack()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Features:Ai"] = "true",
            ["Features:Payment"] = "true",
            ["Features:Upload"] = "true",
            ["Ai:Provider"] = " DeepSeek ",
            ["Billing:Payment:Provider"] = " PayOS ",
            ["Storage:Provider"] = " R2 "
        }).Build();
        var adapters = ProductionAdapterSelection.FromConfiguration(configuration);
        Assert.Equal(Approved, adapters);
        ProductionSafety.ValidateAdapters(true, false, adapters);
    }

    private static ProductionAdapterSelection Approved => new(true, true, "deepseek", "payos", "r2");

    [Fact]
    public void ValidateEmailConfigurationInDevelopmentOrTestingAllowsNoopAndHttp()
    {
        var config = CreateEmailConfig(provider: "noop", publicUrl: "http://localhost:3000");
        ProductionSafety.ValidateEmailConfiguration(false, config);
    }

    [Theory]
    [InlineData("noop")]
    [InlineData("")]
    [InlineData("sendgrid")]
    public void ValidateEmailConfigurationInProductionOrStagingFailsIfProviderIsNotResend(string provider)
    {
        var config = CreateEmailConfig(provider: provider);
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateEmailConfiguration(true, config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-email")]
    [InlineData("test@localhost")]
    public void ValidateEmailConfigurationInProductionOrStagingFailsIfFromAddressIsInvalid(string fromAddress)
    {
        var config = CreateEmailConfig(fromAddress: fromAddress);
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateEmailConfiguration(true, config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateEmailConfigurationInProductionOrStagingFailsIfApiKeyIsMissing(string apiKey)
    {
        var config = CreateEmailConfig(apiKey: apiKey);
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateEmailConfiguration(true, config));
    }

    [Fact]
    public void ValidateEmailConfigurationInProductionOrStagingFailsIfApiBaseUrlIsNotOfficialResend()
    {
        var config = CreateEmailConfig(apiBaseUrl: "https://custom.api.com");
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateEmailConfiguration(true, config));
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://staging.nexora.app")]
    [InlineData("http://localhost:3000")]
    [InlineData("https://localhost:3000")]
    public void ValidateEmailConfigurationInProductionOrStagingFailsIfPublicUrlIsNotHttpsOrIsLoopback(string publicUrl)
    {
        var config = CreateEmailConfig(publicUrl: publicUrl);
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateEmailConfiguration(true, config));
    }

    [Fact]
    public void ValidateEmailConfigurationInProductionOrStagingPassesWhenValid()
    {
        var config = CreateEmailConfig();
        ProductionSafety.ValidateEmailConfiguration(true, config);
    }

    private static IConfiguration CreateEmailConfig(
        string provider = "resend",
        string fromAddress = "support@nexora.app",
        string fromName = "Nexora",
        string apiKey = "re_valid_key_12345",
        string apiBaseUrl = "https://api.resend.com",
        string publicUrl = "https://staging.nexora.app")
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:Provider"] = provider,
                ["Email:FromAddress"] = fromAddress,
                ["Email:FromName"] = fromName,
                ["Email:Resend:ApiKey"] = apiKey,
                ["Email:Resend:ApiBaseUrl"] = apiBaseUrl,
                ["Authentication:EmailVerification:PublicUrl"] = publicUrl
            })
            .Build();
    }
}
