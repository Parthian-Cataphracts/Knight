using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Abstractions.Features;

namespace FeatureManagement;

public static class FeatureManagementModule
{
    public static IServiceCollection AddFeatureManagementModule(this IServiceCollection services)
    {
        services.AddScoped<IFeatureAccessService, FeatureAccessService>();
        services.AddScoped<ITenantFeatureManagementService, TenantFeatureManagementService>();

        return services;
    }
}
