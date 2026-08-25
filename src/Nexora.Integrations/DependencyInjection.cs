using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Storage;
using Nexora.Integrations.Storage;

namespace Nexora.Integrations;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LocalStorageOptions>().Bind(configuration.GetSection(LocalStorageOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.RootPath), "Storage:Local:RootPath is required.")
            .ValidateOnStart();
        services.AddSingleton<IStorageProvider, LocalStorageProvider>();
        return services;
    }
}
