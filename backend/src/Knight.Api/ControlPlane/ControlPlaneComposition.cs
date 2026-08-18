using AccessControl;
using Billing;
using Customers;
using FeatureRegistry;
using Plans;
using Knight.Application.Abstractions.ControlPlane;
using Stores;
using Subscriptions;

namespace Knight.Api.ControlPlane;

/// <summary>
/// Composition for the control plane: the modules plus the request-scoped
/// principal and audit context that adapt HTTP to the application layer.
/// Migrating and seeding live in Infrastructure, where the schema does.
/// </summary>
public static class ControlPlaneComposition
{
    public static IServiceCollection AddControlPlaneModules(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAccessControlModule(configuration);
        services.AddCustomersModule();
        services.AddStoresModule(configuration);
        services.AddFeatureRegistryModule();
        services.AddPlansModule();
        services.AddSubscriptionsModule(configuration);
        services.AddBillingModule(configuration);

        services.AddScoped<IControlPlanePrincipal, HttpControlPlanePrincipal>();
        services.AddScoped<IAuditContext, HttpAuditContext>();

        return services;
    }
}
