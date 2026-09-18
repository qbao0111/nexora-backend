using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Business.Speech;

namespace Nexora.Integrations.Speech;

public static class SpeechDependencyInjection
{
    public static IServiceCollection AddSpeech(this IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("Features:Speech");
        services.AddOptions<AzureSpeechOptions>()
            .Bind(configuration.GetSection(AzureSpeechOptions.SectionName))
            .Validate(options => !enabled || !string.IsNullOrWhiteSpace(options.Key),
                "Speech:Azure:Key is required when Features:Speech is enabled.")
            .Validate(options => !enabled || AzureSpeechOptions.IsValidRegion(options.Region),
                "Speech:Azure:Region must be a valid Azure region when Features:Speech is enabled.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 30,
                "Speech:Azure:TimeoutSeconds must be between 1 and 30.")
            .Validate(options => options.ServerRefreshMinutes > 0 &&
                options.ServerRefreshMinutes < options.ClientUsableMinutes,
                "Speech:Azure:ServerRefreshMinutes must be positive and less than ClientUsableMinutes.")
            .Validate(options => options.ClientUsableMinutes is > 0 and < 10,
                "Speech:Azure:ClientUsableMinutes must be between 1 and 9.")
            .ValidateOnStart();

        services.AddHttpClient(AzureSpeechTokenProvider.HttpClientName, (provider, client) =>
        {
            var settings = provider.GetRequiredService<IOptions<AzureSpeechOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        }).RedactLoggedHeaders([AzureSpeechTokenProvider.SubscriptionKeyHeader]);
        services.AddSingleton<ISpeechTokenProvider, AzureSpeechTokenProvider>();
        return services;
    }
}
