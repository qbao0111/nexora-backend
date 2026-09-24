using Microsoft.Extensions.Configuration;
using Nexora.Integrations;

namespace Nexora.UnitTests;

public sealed class ProductionSafetyTests
{
    [Fact]
    public void ProductionFailsClosedWhileAnyDevelopmentAdapterFeatureIsEnabled()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateDevelopmentAdapters(true, aiEnabled: false, paymentEnabled: true, uploadEnabled: false));
    }

    [Fact]
    public void ProductionStillRejectsPayosUntilDec02IsResolved()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateDevelopmentAdapters(
            true, aiEnabled: false, paymentEnabled: true, uploadEnabled: false, storageProvider: "r2", paymentProvider: "payos"));

        Assert.Contains("payOS payment adapter", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledProductionCapabilitiesAndDevelopmentRemainAvailable()
    {
        ProductionSafety.ValidateDevelopmentAdapters(true, aiEnabled: false, paymentEnabled: false, uploadEnabled: false, storageProvider: "r2");
        ProductionSafety.ValidateDevelopmentAdapters(false, aiEnabled: true, paymentEnabled: true, uploadEnabled: true);
    }

    [Fact]
    public void ProductionAllowsR2WhenUploadIsEnabledAfterDurableUploadFlow()
    {
        ProductionSafety.ValidateDevelopmentAdapters(
            true, aiEnabled: false, paymentEnabled: false, uploadEnabled: true, storageProvider: "r2");
    }

    [Fact]
    public void ProductionAllowsR2WhenUploadIsDisabled()
    {
        ProductionSafety.ValidateDevelopmentAdapters(
            true, aiEnabled: false, paymentEnabled: false, uploadEnabled: false, storageProvider: "r2");
    }

    [Fact]
    public void ProductionRejectsLocalWhenUploadIsEnabled()
    {
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateDevelopmentAdapters(
            true, aiEnabled: false, paymentEnabled: false, uploadEnabled: true, storageProvider: "local"));
    }

    [Fact]
    public void ProductionRejectsLocalEvenWhenUploadIsDisabled()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateDevelopmentAdapters(
            true, aiEnabled: false, paymentEnabled: false, uploadEnabled: false, storageProvider: "local"));
        Assert.Contains("durable storage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingRejectsLocalEvenWhenUploadIsDisabled()
    {
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateDevelopmentAdapters(
            false, aiEnabled: false, paymentEnabled: false, uploadEnabled: false, storageProvider: "local", isStaging: true));
    }

    [Fact]
    public void StagingAllowsR2AndExplicitPersistentLocalVolume()
    {
        ProductionSafety.ValidateDevelopmentAdapters(false, false, false, true, storageProvider: "r2", isStaging: true);
        ProductionSafety.ValidateDevelopmentAdapters(false, false, false, true, storageProvider: "local", isStaging: true,
            localPersistentVolumeConfigured: true);
    }

    [Fact]
    public void DevelopmentAllowsLocalWhenUploadIsEnabled()
    {
        ProductionSafety.ValidateDevelopmentAdapters(
            false, aiEnabled: false, paymentEnabled: false, uploadEnabled: true, storageProvider: "local");
    }

    [Fact]
    public void UnknownStorageProviderFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateDevelopmentAdapters(
            false, aiEnabled: false, paymentEnabled: false, uploadEnabled: false, storageProvider: "wat"));
    }

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
