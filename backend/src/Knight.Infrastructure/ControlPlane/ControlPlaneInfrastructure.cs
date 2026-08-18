using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Infrastructure.ControlPlane.Adapters;
using Knight.Infrastructure.ControlPlane.Repositories;
using Knight.Infrastructure.ControlPlane.Security;
using Knight.Infrastructure.ControlPlane.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Knight.Infrastructure.ControlPlane;

/// <summary>
/// Wires the control-plane persistence and security services. Deliberately
/// separate from <c>AddPlatformInfrastructure</c>: the two schemas are
/// independent, and the legacy registration will be deleted wholesale in
/// phase 8 without disturbing anything here.
/// </summary>
public static class ControlPlaneInfrastructure
{
    public static IServiceCollection AddControlPlaneInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        // Blank counts as missing, not as a value: configuration files carry the
        // key with an empty string precisely so the real one comes from the
        // environment, and a "" that survived to Npgsql fails much later and far
        // less clearly than it does here.
        var connectionString = FirstConfigured(
            configuration.GetConnectionString("ControlPlane"),
            configuration.GetConnectionString("Platform"))
            ?? throw new InvalidOperationException(
                "Missing connection string 'ControlPlane'. Set it via configuration or the CONTROL_PLANE_DB_CONNECTION_STRING environment variable.");

        // AddDbContext, never pooled: the customer-isolation filter closes over
        // the request's scope, and a pooled instance could carry one request's
        // customer into another's.
        services.AddDbContext<ControlPlaneDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__ef_migrations_history", ControlPlaneDbContext.SchemaName)));

        services.AddScoped<CustomerScopeAccessor>();
        services.AddScoped<ICustomerScope>(sp => sp.GetRequiredService<CustomerScopeAccessor>());
        services.AddScoped<ICustomerScopeAccessor>(sp => sp.GetRequiredService<CustomerScopeAccessor>());

        services.AddScoped<Customers.Domain.ICustomerRepository, ControlPlaneCustomerRepository>();
        services.AddScoped<Stores.Domain.IStoreRepository, ControlPlaneStoreRepository>();
        services.AddScoped<IControlPlaneUserRepository, ControlPlaneUserRepository>();
        services.AddScoped<IRoleRepository, ControlPlaneRoleRepository>();
        services.AddScoped<IUserSessionRepository, UserSessionRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();

        services.AddScoped<FeatureRegistry.Domain.IFeatureRepository, FeatureRepository>();
        services.AddScoped<Plans.Domain.IPlanRepository, PlanRepository>();
        services.AddScoped<Plans.Domain.IFeaturePriceRepository, FeaturePriceRepository>();
        services.AddScoped<Subscriptions.Domain.ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<Subscriptions.Domain.IFeatureEntitlementRepository, FeatureEntitlementRepository>();
        services.AddScoped<Billing.Domain.IBillingAccountRepository, BillingAccountRepository>();
        services.AddScoped<Billing.Domain.IInvoiceRepository, InvoiceRepository>();

        // Ports that let one control-plane module read another's data without
        // referencing it.
        services.AddScoped<IPlanCatalogReader, PlanCatalogReader>();
        services.AddScoped<IFeatureCatalogReader, FeatureCatalogReader>();
        services.AddScoped<IPricingReader, PricingReader>();
        services.AddScoped<ISubscriptionReader, SubscriptionReader>();
        services.AddScoped<IStoreHostingReader, StoreHostingReader>();
        services.AddScoped<IEntitlementEventPublisher, LoggingEntitlementEventPublisher>();

        services.AddSingleton<IControlPlanePasswordHasher, ControlPlanePasswordHasher>();
        services.AddSingleton<ISecureTokenFactory, SecureTokenFactory>();
        services.AddSingleton<IControlPlaneTokenGenerator, ControlPlaneTokenGenerator>();
        services.AddSingleton<ITotpService, TotpService>();

        services.AddScoped<ICommercialCatalogueSeeder, CommercialCatalogueSeeder>();

        return services;
    }

    private static string? FirstConfigured(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
