using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexora.Business.Email;

namespace Nexora.Integrations.Email;

public static class EmailServiceCollectionExtensions
{
    public static IServiceCollection AddEmail(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var configuredProvider = configuration
            .GetValue<string?>($"{EmailOptions.SectionName}:Provider")?
            .Trim()
            .ToLowerInvariant() ?? EmailConfigurationRules.NoopProvider;
        if (!EmailConfigurationRules.IsSupportedProvider(configuredProvider))
            throw new InvalidOperationException("Email:Provider must be noop or resend.");

        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .Validate(options => EmailConfigurationRules.IsSupportedProvider(options.Provider),
                "Email:Provider must be noop or resend.")
            .Validate(options => !string.Equals(options.Provider, EmailConfigurationRules.ResendProvider, StringComparison.OrdinalIgnoreCase) ||
                EmailConfigurationRules.IsValidFromAddress(options.FromAddress),
                "Email:FromAddress must be a valid domain email address when Resend is enabled.")
            .Validate(options => !string.Equals(options.Provider, EmailConfigurationRules.ResendProvider, StringComparison.OrdinalIgnoreCase) ||
                EmailConfigurationRules.IsValidDisplayName(options.FromName),
                "Email:FromName is required and must not contain control characters when Resend is enabled.")
            .ValidateOnStart();

        services.AddOptions<ResendEmailOptions>()
            .Bind(configuration.GetSection(ResendEmailOptions.SectionName))
            .Validate(options => !string.Equals(configuredProvider, EmailConfigurationRules.ResendProvider, StringComparison.OrdinalIgnoreCase) ||
                EmailConfigurationRules.IsValidApiKey(options.ApiKey),
                "Email:Resend:ApiKey must be supplied through secret configuration when Resend is enabled.")
            .Validate(options => !string.Equals(configuredProvider, EmailConfigurationRules.ResendProvider, StringComparison.OrdinalIgnoreCase) ||
                EmailConfigurationRules.IsOfficialResendBaseUrl(options.ApiBaseUrl),
                "Email:Resend:ApiBaseUrl must be the official HTTPS Resend API endpoint.")
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60,
                "Email:Resend:TimeoutSeconds must be between 1 and 60 seconds.")
            .ValidateOnStart();

        services.AddHttpClient<ResendEmailSender>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ResendEmailOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        });
        services.AddSingleton<NoOpEmailSender>();
        services.AddSingleton<IEmailSender>(serviceProvider =>
            string.Equals(configuredProvider, EmailConfigurationRules.ResendProvider, StringComparison.OrdinalIgnoreCase)
                ? serviceProvider.GetRequiredService<ResendEmailSender>()
                : serviceProvider.GetRequiredService<NoOpEmailSender>());
        return services;
    }
}
