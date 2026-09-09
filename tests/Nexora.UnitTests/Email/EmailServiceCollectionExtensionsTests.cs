using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Business.Email;
using Nexora.Integrations.Email;

namespace Nexora.UnitTests.Email;

public sealed class EmailServiceCollectionExtensionsTests
{
    [Fact]
    public void UnknownProviderFailsClosedDuringRegistration()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "smtp"
        });

        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddEmail(configuration));

        Assert.Equal("Email:Provider must be noop or resend.", exception.Message);
    }

    [Fact]
    public void ResendMissingApiKeyFailsWhenOptionsAreResolved()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "resend",
            ["Email:FromAddress"] = "no-reply@example.test",
            ["Email:FromName"] = "Nexora",
            ["Email:Resend:ApiKey"] = ""
        });
        var services = new ServiceCollection();
        services.AddEmail(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<ResendEmailOptions>>().Value);

        Assert.Contains("Email:Resend:ApiKey", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResendMissingFromDomainFailsWhenOptionsAreResolved()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "resend",
            ["Email:FromAddress"] = "no-reply",
            ["Email:FromName"] = "Nexora",
            ["Email:Resend:ApiKey"] = "test-only-email-key"
        });
        var services = new ServiceCollection();
        services.AddEmail(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<EmailOptions>>().Value);

        Assert.Contains("Email:FromAddress", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NoopProviderIsSafeDefaultAndResolvesNoOpSender()
    {
        var services = new ServiceCollection();
        services.AddEmail(Configuration(new Dictionary<string, string?>()));
        using var provider = services.BuildServiceProvider();

        var sender = provider.GetRequiredService<IEmailSender>();

        Assert.IsType<NoOpEmailSender>(sender);
    }

    [Fact]
    public void ResendProviderResolvesTypedAdapterWithoutCallingNetwork()
    {
        var services = new ServiceCollection();
        services.AddEmail(Configuration(new Dictionary<string, string?>
        {
            ["Email:Provider"] = "resend",
            ["Email:FromAddress"] = "no-reply@example.test",
            ["Email:FromName"] = "Nexora",
            ["Email:Resend:ApiKey"] = "test-only-email-key"
        }));
        using var provider = services.BuildServiceProvider();

        var sender = provider.GetRequiredService<IEmailSender>();

        Assert.IsType<ResendEmailSender>(sender);
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
