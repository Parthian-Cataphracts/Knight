using Knight.Application.Abstractions.ControlPlane;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureRegistry;

public static class FeatureRegistryModule
{
    public static IServiceCollection AddFeatureRegistryModule(this IServiceCollection services)
    {
        services.AddScoped<IFeatureCatalogService, FeatureCatalogService>();
        services.AddScoped<IFeatureVersionService, FeatureVersionService>();

        // The registry owns dependency resolution and lends it to the delivery
        // engine through an application-layer port, so the two modules never
        // reference each other.
        services.AddScoped<IFeaturePlanResolver, FeaturePlanResolver>();

        return services;
    }
}
