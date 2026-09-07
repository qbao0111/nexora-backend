using Microsoft.Extensions.DependencyInjection;
using Nexora.Business.Authorization;

namespace Nexora.Business;

public static class DependencyInjection
{
    public static IServiceCollection AddBusiness(this IServiceCollection services)
    {
        services.AddSingleton<IOwnershipAuthorizer, OwnershipAuthorizer>();
        services.AddSingleton<Nexora.Business.Ai.IStructuredAiExecutor, Nexora.Business.Ai.StructuredAiExecutor>();
        return services;
    }
}
