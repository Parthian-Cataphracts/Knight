using AccessControl;
using Billing;
using Customers;
using FeatureDelivery;
using FeatureRegistry;
using Ingestion;
using Knight.Api.BackgroundServices;
using Knight.Api.Ingest;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.Caching;
using Observability;
using Plans;
using Provisioning;
using Servers;
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
        services.AddFeatureDeliveryModule(configuration);
        services.AddServersModule(configuration);
        services.AddProvisioningModule(configuration);
        services.AddPlansModule();
        services.AddSubscriptionsModule(configuration);
        services.AddBillingModule(configuration);
        services.AddIngestionModule(configuration);
        services.AddObservabilityModule(configuration);

        services.AddScoped<IControlPlanePrincipal, HttpControlPlanePrincipal>();
        services.AddScoped<IStorePrincipal, HttpStorePrincipal>();
        services.AddScoped<IAuditContext, HttpAuditContext>();

        // Refuses to start a non-development host with no Redis, where replay
        // protection would silently degrade to per-instance memory.
        services.AddHostedService<ReplayGuardGuardrail>();
        services.AddHostedService<StoreHealthPoller>();

        // Recovers installation jobs whose agent stopped reporting. Without it a
        // dead agent holds its store's queue forever and every later install for
        // that store silently never happens.
        services.AddHostedService<FeatureJobSweeper>();

        // The only thing that can decide a machine is offline: absence cannot be
        // reported by the thing that is absent.
        services.AddHostedService<FleetMonitor>();

        // Provisioning waits on facts that arrive from five other modules and
        // notify nobody, so something has to re-ask on a timer.
        services.AddHostedService<ProvisioningCoordinator>();
        services.AddHostedService<RolloutCoordinator>();

        // Evaluates the rules nobody can evaluate at the moment something
        // happens — spikes, entitlements that were never installed, drift — and
        // drains the notification queue.
        services.AddHostedService<ObservabilityWorker>();

        return services;
    }
}
