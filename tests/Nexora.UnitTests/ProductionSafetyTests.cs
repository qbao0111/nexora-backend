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
    public void ProductionRejectsLocalStorageEvenWhenUploadIsDisabled()
    {
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateDevelopmentAdapters(
            true, aiEnabled: false, paymentEnabled: false, uploadEnabled: false, storageProvider: "local"));
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
    public void DeploymentConfigurationAcceptsCompleteProductionLikeR2Configuration()
    {
        ProductionSafety.ValidateDeploymentConfiguration("Production", CreateDeploymentConfig());
    }

    [Fact]
    public void DevelopmentAndTestingDoNotRequireSentryOrProductionConfiguration()
    {
        ProductionSafety.ValidateDeploymentConfiguration("Development", new ConfigurationBuilder().Build());
        ProductionSafety.ValidateDeploymentConfiguration("Testing", new ConfigurationBuilder().Build());
    }

    [Fact]
    public void DeploymentConfigurationRejectsMissingPostgres()
    {
        var configuration = CreateDeploymentConfig();
        configuration["ConnectionStrings:Postgres"] = "";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateDeploymentConfiguration("Staging", configuration));
        Assert.DoesNotContain("password", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DeploymentConfigurationRejectsMissingSentryDsn()
    {
        var configuration = CreateDeploymentConfig();
        configuration["Sentry:Dsn"] = "";
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateDeploymentConfiguration("Production", configuration));
    }

    [Fact]
    public void DeploymentConfigurationRejectsMissingSentryReleaseWithoutRenderCommit()
    {
        var configuration = CreateDeploymentConfig();
        configuration["Sentry:Release"] = "";
        configuration["RENDER_GIT_COMMIT"] = "";
        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateDeploymentConfiguration("Production", configuration));
    }

    [Fact]
    public void DeploymentConfigurationAcceptsExplicitSentryRelease()
    {
        var configuration = CreateDeploymentConfig(renderCommit: "render-commit");
        configuration["Sentry:Release"] = "explicit-release";
        ProductionSafety.ValidateSentryConfiguration(configuration);
    }

    [Fact]
    public void RenderCommitIsUsedWhenSentryReleaseIsNotExplicit()
    {
        Assert.Equal("render-commit", ProductionSafety.ResolveSentryRelease(" ", " render-commit "));
        Assert.Equal("explicit-release", ProductionSafety.ResolveSentryRelease(" explicit-release ", "render-commit"));
        Assert.Null(ProductionSafety.ResolveSentryRelease(null, " "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("nexora-local-development-only-signing-key-change-me")]
    [InlineData("replace-with-at-least-32-random-characters")]
    public void DeploymentConfigurationRejectsMissingOrWeakJwtSigningKey(string signingKey)
    {
        var configuration = CreateDeploymentConfig(signingKey: signingKey);
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateJwtConfiguration(configuration));
    }

    [Fact]
    public void ProductionR2ConfigurationDoesNotExposeCredentialValues()
    {
        var configuration = CreateDeploymentConfig();
        configuration["Storage:R2:Bucket"] = "";
        configuration["Storage:R2:SecretAccessKey"] = "super-secret-value";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateStorageConfiguration(configuration));
        Assert.DoesNotContain("super-secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionR2ConfigurationRejectsTemplateValues()
    {
        var configuration = CreateDeploymentConfig();
        configuration["Storage:R2:Bucket"] = "replace-with-private-bucket-name";

        Assert.Throws<InvalidOperationException>(() =>
            ProductionSafety.ValidateStorageConfiguration(configuration));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("http://frontend.example")]
    [InlineData("https://localhost:3000")]
    [InlineData("https://127.0.0.1:3000")]
    [InlineData("https://frontend.example/path")]
    [InlineData("https://frontend.example/?query=1")]
    [InlineData("https://frontend.example/#fragment")]
    [InlineData("https://user:password@frontend.example")]
    [InlineData(" https://frontend.example")]
    public void ProductionFrontendOriginsRejectUnsafeValues(string origin)
    {
        var configuration = CreateDeploymentConfig(frontendOrigins: [origin]);
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateFrontendOrigins("Production", configuration));
    }

    [Fact]
    public void ProductionFrontendOriginsRequireAtLeastOneConfiguredOrigin()
    {
        var configuration = CreateDeploymentConfig(frontendOrigins: []);
        Assert.Throws<InvalidOperationException>(() => ProductionSafety.ValidateFrontendOrigins("Production", configuration));
    }

    [Fact]
    public void ProductionFrontendOriginsNormalizeAndDeduplicateSafeValues()
    {
        var configuration = CreateDeploymentConfig(frontendOrigins: ["HTTPS://Frontend.Example/", "https://frontend.example"]);
        var origins = ProductionSafety.ValidateFrontendOrigins("Production", configuration);
        var origin = Assert.Single(origins);
        Assert.Equal("https://frontend.example", origin);
    }

    [Fact]
    public void FrontendOriginAllowlistUsesTheSameNormalizationForCorsAndCsrf()
    {
        Assert.True(ProductionSafety.IsAllowedFrontendOrigin(
            "HTTPS://Frontend.Example",
            ["https://frontend.example/"]));
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
    public void ValidateEmailConfigurationInProductionOrStagingRejectsTemplateApiKey()
    {
        var config = CreateEmailConfig(apiKey: "replace-with-resend-api-key");

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

    private static IConfiguration CreateDeploymentConfig(
        string storageProvider = "r2",
        string connectionString = "Host=postgres.example;Database=nexora",
        string signingKey = "deployment-signing-key-material-32-characters-minimum",
        string dsn = "https://public@example.invalid/1",
        string release = "deployment-release",
        string? renderCommit = null,
        string[]? frontendOrigins = null)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = connectionString,
            ["Authentication:Jwt:SigningKey"] = signingKey,
            ["Storage:Provider"] = storageProvider,
            ["Storage:R2:AccountId"] = "account-id",
            ["Storage:R2:Bucket"] = "private-bucket",
            ["Storage:R2:AccessKeyId"] = "access-key",
            ["Storage:R2:SecretAccessKey"] = "secret-key",
            ["Storage:R2:Endpoint"] = "https://account-id.r2.cloudflarestorage.com",
            ["Email:Provider"] = "resend",
            ["Email:FromAddress"] = "support@nexora.example",
            ["Email:FromName"] = "Nexora",
            ["Email:Resend:ApiKey"] = "resend-api-key",
            ["Email:Resend:ApiBaseUrl"] = "https://api.resend.com",
            ["Authentication:EmailVerification:PublicUrl"] = "https://frontend.example",
            ["Sentry:Dsn"] = dsn,
            ["Sentry:Release"] = release
        };
        if (renderCommit is not null)
            configuration["RENDER_GIT_COMMIT"] = renderCommit;

        var origins = frontendOrigins ?? ["https://frontend.example"];
        for (var index = 0; index < origins.Length; index++)
            configuration[$"Frontend:AllowedOrigins:{index}"] = origins[index];

        return new ConfigurationBuilder()
            .AddInMemoryCollection(configuration)
            .Build();
    }
}
