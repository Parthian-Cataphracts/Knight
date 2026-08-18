using Microsoft.Extensions.DependencyInjection;

namespace FeatureRegistry;

public static class FeatureRegistryModule
{
    public static IServiceCollection AddFeatureRegistryModule(this IServiceCollection services)
    {
        services.AddScoped<IFeatureCatalogService, FeatureCatalogService>();

        return services;
    }
}
