using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Nexora.Business.Ai;
using Nexora.Business.Billing;
using Nexora.Business.Storage;
using Nexora.Integrations.Ai;
using Nexora.Integrations.Payments;
using Nexora.Integrations.Storage;

namespace Nexora.IntegrationTests;

public sealed class ProductionAdapterStartupTests
{
    [Fact]
    public async Task ApprovedProductionStackStartsWithRealAdaptersAndNoProviderCalls()
    {
        var transport = new RejectExternalCallsHandler();
        var configuration = ApprovedConfiguration();
        configuration["Ai:Provider"] = " DeepSeek ";
        configuration["Billing:Payment:Provider"] = " PayOS ";
        configuration["Storage:Provider"] = " R2 ";
        using var factory = new NexoraApiFactory("Production", configuration, services =>
        {
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = transport));
            services.RemoveAll<IAiProvider>();
            services.AddSingleton<IAiProvider>(provider => provider.GetRequiredService<DeepSeekAiProvider>());
        });
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.IsType<DeepSeekAiProvider>(factory.Services.GetRequiredService<IAiProvider>());
        Assert.IsType<PayosPaymentProvider>(factory.Services.GetRequiredService<IPaymentProvider>());
        Assert.IsType<R2StorageProvider>(factory.Services.GetRequiredService<IStorageProvider>());
        using var swagger = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.NotFound, swagger.StatusCode);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData("Ai:DeepSeek:ApiKey", "")]
    [InlineData("Ai:DeepSeek:BaseUrl", "https://example.test")]
    [InlineData("Ai:DeepSeek:BaseUrl", "http://api.deepseek.com")]
    [InlineData("Ai:DeepSeek:Model", "")]
    [InlineData("Ai:DeepSeek:TimeoutSeconds", "0")]
    [InlineData("Ai:DeepSeek:TimeoutSeconds", "121")]
    [InlineData("Ai:DeepSeek:MaxAttempts", "2")]
    [InlineData("Ai:DeepSeek:RetryBaseDelayMilliseconds", "5001")]
    [InlineData("Ai:DeepSeek:Reasoning:InterviewEvaluate:Thinking", "unknown")]
    [InlineData("Ai:DeepSeek:Reasoning:InterviewEvaluate:Effort", "unknown")]
    [InlineData("Billing:Payos:ClientId", "")]
    [InlineData("Billing:Payos:ApiKey", "")]
    [InlineData("Billing:Payos:ChecksumKey", "")]
    [InlineData("Billing:Payos:ReturnUrl", "")]
    [InlineData("Billing:Payos:CancelUrl", "")]
    [InlineData("Billing:Payos:ReturnUrl", "http://example.test/payment/success")]
    [InlineData("Billing:Payos:CancelUrl", "http://localhost/payment/cancel")]
    [InlineData("Billing:Payos:ReturnUrl", "https://localhost/payment/success")]
    [InlineData("Billing:Payos:ReturnUrl", "https://user:password@example.test/payment/success")]
    [InlineData("Billing:Payos:CancelUrl", "https://example.test/payment/cancel#fragment")]
    [InlineData("Billing:Payos:TimeoutSeconds", "4")]
    [InlineData("Billing:Payos:TimeoutSeconds", "61")]
    public void InvalidApprovedProviderOptionsFailDuringApplicationStartup(string key, string value)
    {
        var configuration = ApprovedConfiguration();
        configuration[key] = value;
        using var factory = new NexoraApiFactory("Production", configuration);

        var error = Assert.Throws<OptionsValidationException>(() => factory.CreateHttpsClient());
        Assert.Contains(key.StartsWith("Ai:", StringComparison.Ordinal) ? "DeepSeek" : "Billing:Payos",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledProductionCapabilitiesCanStartWithoutDeepSeekOrPayosCredentials()
    {
        var configuration = ApprovedConfiguration();
        configuration["Features:Ai"] = "false";
        configuration["Features:Payment"] = "false";
        configuration["Ai:DeepSeek:ApiKey"] = "";
        configuration["Billing:Payos:ClientId"] = "";
        configuration["Billing:Payos:ApiKey"] = "";
        configuration["Billing:Payos:ChecksumKey"] = "";
        using var factory = new NexoraApiFactory("Production", configuration);
        using var client = factory.CreateHttpsClient();

        using var response = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("Ai:Provider")]
    [InlineData("Billing:Payment:Provider")]
    public void UnknownSelectorsStillFailConfigurationValidationWhenFeaturesAreDisabled(string key)
    {
        var configuration = ApprovedConfiguration();
        configuration["Features:Ai"] = "false";
        configuration["Features:Payment"] = "false";
        configuration[key] = "unknown";
        using var factory = new NexoraApiFactory("Production", configuration);

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateHttpsClient());
        Assert.Contains(key, error.Message, StringComparison.Ordinal);
    }

    private static Dictionary<string, string?> ApprovedConfiguration() => new()
    {
        ["Features:Ai"] = "true",
        ["Features:Payment"] = "true",
        ["Features:Upload"] = "true",
        ["Features:Speech"] = "false",
        ["Ai:Provider"] = "deepseek",
        ["Ai:DeepSeek:ApiKey"] = "test-only-deepseek-key-not-used",
        ["Ai:DeepSeek:BaseUrl"] = "https://api.deepseek.com",
        ["Ai:DeepSeek:Model"] = "deepseek-v4-flash",
        ["Ai:DeepSeek:MaxAttempts"] = "1",
        ["Billing:Payment:Provider"] = "payos",
        ["Billing:Payos:ClientId"] = "test-only-client-id",
        ["Billing:Payos:ApiKey"] = "test-only-api-key",
        ["Billing:Payos:ChecksumKey"] = "test-only-checksum-key",
        ["Billing:Payos:ReturnUrl"] = "https://frontend.example.test/payment/success",
        ["Billing:Payos:CancelUrl"] = "https://frontend.example.test/payment/cancel"
    };

    private sealed class RejectExternalCallsHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Production startup must not contact an external provider.");
        }
    }
}
