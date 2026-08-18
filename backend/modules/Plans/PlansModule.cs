using Microsoft.Extensions.DependencyInjection;

namespace Plans;

/// <summary>
/// The plan catalogue's seed data is loaded by Infrastructure, not from here:
/// seeding touches both plans and feature identities, and a module may not reach
/// into a sibling to do it.
/// </summary>
public static class PlansModule
{
    public static IServiceCollection AddPlansModule(this IServiceCollection services)
    {
        services.AddScoped<IPricingCalculator, PricingCalculator>();
        services.AddScoped<IPlanService, PlanService>();

        return services;
    }
}
