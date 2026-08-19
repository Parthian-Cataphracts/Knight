using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Security;
using Knight.Infrastructure.Caching;
using Knight.Infrastructure.ControlPlane.Adapters;
using Knight.Infrastructure.ControlPlane.Integration;
using Knight.Infrastructure.ControlPlane.Repositories;
using Knight.Infrastructure.ControlPlane.Security;
using Knight.Infrastructure.ControlPlane.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

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
        services.AddScoped<Stores.Domain.IStoreTelemetryRepository, StoreTelemetryRepository>();
        services.AddScoped<Ingestion.Domain.IIngestionRepository, IngestionRepository>();
        services.AddScoped<IControlPlaneUserRepository, ControlPlaneUserRepository>();
        services.AddScoped<IRoleRepository, ControlPlaneRoleRepository>();
        services.AddScoped<IUserSessionRepository, UserSessionRepository>();
        services.AddScoped<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<IControlPlaneOverviewReader, ControlPlaneOverviewReader>();
        services.AddScoped<ICustomerDirectoryReader, CustomerDirectoryReader>();
        services.AddScoped<IPlanSubscriberReader, PlanSubscriberReader>();
        services.AddScoped<IFeatureUsageReader, FeatureUsageReader>();
        services.AddScoped<ILabelReader, LabelReader>();

        services.AddScoped<FeatureRegistry.Domain.IFeatureRepository, FeatureRepository>();
        services.AddScoped<Plans.Domain.IPlanRepository, PlanRepository>();
        services.AddScoped<Plans.Domain.IFeaturePriceRepository, FeaturePriceRepository>();
        services.AddScoped<Subscriptions.Domain.ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<Subscriptions.Domain.IFeatureEntitlementRepository, FeatureEntitlementRepository>();
        services.AddScoped<Billing.Domain.IBillingAccountRepository, BillingAccountRepository>();
        services.AddScoped<Billing.Domain.IInvoiceRepository, InvoiceRepository>();

        services.AddScoped<FeatureRegistry.Domain.IFeatureVersionRepository, FeatureVersionRepository>();
        services.AddScoped<FeatureDelivery.Domain.IFeatureInstallationRepository, FeatureInstallationRepository>();
        services.AddScoped<FeatureDelivery.Domain.IFeatureInstallationJobRepository, FeatureInstallationJobRepository>();
        services.AddScoped<FeatureDelivery.Domain.IFeatureConfigurationRepository, FeatureConfigurationRepository>();

        // Both of these are joins between the registry and delivery, which are
        // not allowed to know about each other, so they live here.
        services.AddScoped<IStoreDeliveryReader, StoreDeliveryReader>();
        services.AddScoped<FeatureDelivery.IFeatureVersionReader, FeatureVersionReader>();

        services.AddOptions<FeatureArtifactOptions>()
            .Bind(configuration.GetSection(FeatureArtifactOptions.SectionName));

        services.AddScoped<IFeatureArtifactSigner, EcdsaArtifactSigner>();
        services.AddScoped<IFeatureArtifactStore, FileSystemArtifactStore>();
        services.AddSingleton<ISecretProtector>(provider =>
            new AesGcmSecretProtector(ResolveSecretKey(configuration, provider.GetService<IHostEnvironment>())));

        // Ports that let one control-plane module read another's data without
        // referencing it.
        services.AddScoped<IPlanCatalogReader, PlanCatalogReader>();
        services.AddScoped<IFeatureCatalogReader, FeatureCatalogReader>();
        services.AddScoped<IPricingReader, PricingReader>();
        services.AddScoped<ISubscriptionReader, SubscriptionReader>();
        services.AddScoped<IStoreHostingReader, StoreHostingReader>();
        services.AddScoped<ICustomerStatusReader, CustomerStatusReader>();
        services.AddScoped<ICustomerEntitlementReader, CustomerEntitlementReader>();
        services.AddScoped<IEntitlementEventPublisher, DeliveryEntitlementEventPublisher>();

        // Everything the store link needs to leave the process: the token it
        // hands out, the key it signs with, and the guarded HTTP client it calls
        // stores on. The address policy and the outbound client are registered
        // together on purpose — there is no way to make a store call that skips
        // the policy.
        services.AddOptions<StoreProbeOptions>()
            .Bind(configuration.GetSection(StoreProbeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSharedInfrastructureCache(configuration);
        services.AddSingleton<IStoreTokenIssuer, StoreTokenIssuer>();
        services.AddSingleton<IStorePayloadSigner, StorePayloadSigner>();
        services.AddSingleton<IOutboundAddressPolicy, OutboundAddressPolicy>();
        services.AddSingleton<StoreEndpointResolver>();
        services.AddSingleton<IStoreHealthProbe, StoreHealthProbe>();
        services.AddSingleton<IDomainOwnershipVerifier, DomainOwnershipVerifier>();
        services.AddStoreOutboundHttp();

        // The control plane's security primitives are adapters over the shared
        // implementations, so those have to exist even in a host that wires
        // nothing else — the bootstrap tool is exactly that host, and phase 8
        // will make the API one too. TryAdd keeps the legacy registration
        // authoritative wherever both are present.
        services.TryAddSingleton<Identity.Abstractions.IPasswordHasher, Knight.Infrastructure.Security.Pbkdf2PasswordHasher>();
        services.TryAddSingleton<Identity.Abstractions.IRefreshTokenGenerator, Knight.Infrastructure.Security.RefreshTokenGenerator>();

        services.AddSingleton<IControlPlanePasswordHasher, ControlPlanePasswordHasher>();
        services.AddSingleton<ISecureTokenFactory, SecureTokenFactory>();
        services.AddSingleton<IControlPlaneTokenGenerator, ControlPlaneTokenGenerator>();
        services.AddSingleton<ITotpService, TotpService>();

        services.AddScoped<ICommercialCatalogueSeeder, CommercialCatalogueSeeder>();

        return services;
    }

    private static string? FirstConfigured(params string?[] candidates) =>
        candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));

    /// <summary>
    /// The key that encrypts feature configuration secrets.
    ///
    /// Derived from the configured value with SHA-256 rather than used raw, so a
    /// deployment that sets a passphrase instead of exactly 32 random bytes still
    /// gets a full-length key rather than an argument exception at startup.
    ///
    /// The environment comes from <see cref="IHostEnvironment"/> and not from a
    /// configuration key. Reading "ASPNETCORE_ENVIRONMENT" out of configuration
    /// looks equivalent and is not: a test host sets the environment on the host
    /// builder without that key ever appearing in configuration, so the guard
    /// below would have refused to start every integration test.
    ///
    /// Development and Testing fall back to a fixed key with no ceremony. Local
    /// secrets are not secrets, and making developers manage one only teaches
    /// them to paste a real key into a config file.
    /// </summary>
    private static byte[] ResolveSecretKey(IConfiguration configuration, IHostEnvironment? environment)
    {
        var configured = FirstConfigured(
            configuration["FeatureArtifacts:SecretProtectionKey"],
            configuration["Security:SecretProtectionKey"]);

        if (string.IsNullOrWhiteSpace(configured))
        {
            var isLocal = environment is null
                || environment.IsDevelopment()
                || environment.IsEnvironment("Testing");

            if (!isLocal)
            {
                throw new InvalidOperationException(
                    "FeatureArtifacts:SecretProtectionKey must be configured outside Development. " +
                    "Feature configuration secrets cannot be stored without it.");
            }

            configured = "knight-development-secret-protection-key";
        }

        return System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(configured));
    }
}
