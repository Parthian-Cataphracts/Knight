using AccessControl.Abstractions;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Security;
using Knight.Infrastructure.Caching;
using Knight.Infrastructure.ControlPlane.Adapters;
using Knight.Infrastructure.ControlPlane.Caching;
using Knight.Infrastructure.ControlPlane.Integration;
using Knight.Infrastructure.ControlPlane.Repositories;
using Knight.Infrastructure.ControlPlane.Security;
using Knight.Infrastructure.ControlPlane.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
            configuration.GetConnectionString("ControlPlane"))
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
        services.AddScoped<IStoreFeatureCountReader, StoreFeatureCountReader>();
        services.AddScoped<ILabelReader, LabelReader>();
        services.AddScoped<IInsightReader, InsightReader>();
        services.AddScoped<Customers.Domain.ICustomerNoteRepository, CustomerNoteRepository>();

        services.AddScoped<FeatureRegistry.Domain.IFeatureRepository, FeatureRepository>();
        services.AddScoped<Plans.Domain.IPlanRepository, PlanRepository>();
        services.AddScoped<Plans.Domain.IFeaturePriceRepository, FeaturePriceRepository>();
        services.AddScoped<Subscriptions.Domain.ISubscriptionRepository, SubscriptionRepository>();
        services.AddScoped<Subscriptions.Domain.IFeatureEntitlementRepository, FeatureEntitlementRepository>();
        services.AddScoped<Billing.Domain.IBillingAccountRepository, BillingAccountRepository>();
        services.AddScoped<Billing.Domain.IInvoiceRepository, InvoiceRepository>();
        services.AddScoped<PlatformBilling.Domain.IPlatformBillingTransactionRepository, PlatformBillingTransactionRepository>();
        services.AddScoped<PlatformBilling.Domain.ICheckoutSessionRepository, CheckoutSessionRepository>();

        services.AddScoped<FeatureRegistry.Domain.IFeatureVersionRepository, FeatureVersionRepository>();
        services.AddScoped<FeatureRegistry.Domain.IStoreImageRepository, StoreImageRepository>();
        services.AddScoped<FeatureDelivery.Domain.IFeatureInstallationRepository, FeatureInstallationRepository>();
        services.AddScoped<FeatureDelivery.Domain.IFeatureInstallationJobRepository, FeatureInstallationJobRepository>();
        services.AddScoped<FeatureDelivery.Domain.IFeatureRolloutRepository, FeatureRolloutRepository>();
        services.AddScoped<FeatureDelivery.Domain.IFeatureConfigurationRepository, FeatureConfigurationRepository>();

        services.AddScoped<Servers.Domain.IServerRepository, ServerRepository>();
        services.AddScoped<Servers.Domain.IAgentRepository, AgentRepository>();
        services.AddScoped<Servers.Domain.IServerMetricRepository, ServerMetricRepository>();
        services.AddScoped<Servers.Domain.IAlertRepository, AlertRepository>();

        services.AddScoped<Observability.Domain.IErrorGroupRepository, ErrorGroupRepository>();
        services.AddScoped<Observability.Domain.IErrorGroupEventReader, ErrorGroupEventReader>();
        services.AddScoped<Observability.Domain.IIncidentRepository, IncidentRepository>();
        services.AddScoped<Observability.Domain.INotificationRepository, NotificationRepository>();

        // Webhooks reuse the store poller's hardened client rather than getting
        // one of their own: a webhook URL is untrusted input in exactly the way a
        // store URL is (docs/security-threat-model.md, SSRF).
        services.AddScoped<Observability.Domain.INotificationTransport, Integration.NotificationTransport>();

        // Mail leaves KNIGHT through one place, and it is off unless a host is
        // configured — everything that would have sent some then says so rather
        // than pretending.
        services.AddOptions<Integration.EmailOptions>()
            .Bind(configuration.GetSection(Integration.EmailOptions.SectionName))
            .ValidateDataAnnotations();

        services.AddScoped<IEmailSender, Integration.SmtpEmailSender>();
        services.AddScoped<AccessControl.IAccountInvitationSender, Integration.AccountInvitationSender>();
        services.AddScoped<Onboarding.IVerificationEmailSender, Integration.VerificationEmailSender>();

        // The four delivery rules compare records owned by different modules, so
        // the comparison lives here, where the whole schema is visible.
        services.AddScoped<IDeliveryHealthReader, DeliveryHealthReader>();

        // The overdue-backup rule is about an absence, so it reads the store and
        // backup tables together rather than being told by anybody.
        services.AddScoped<IBackupHealthReader, BackupHealthReader>();
        services.AddScoped<IServerPlacementReader, ServerPlacementReader>();

        // Provisioning touches more modules than anything else in the system, so
        // every one of its reads and writes crosses this boundary explicitly.
        services.AddScoped<Provisioning.Domain.IProvisioningJobRepository, ProvisioningJobRepository>();
        services.AddScoped<IStoreProvisioningPort, StoreProvisioningPort>();
        services.AddScoped<IServerProvisioningPort, ServerProvisioningPort>();
        services.AddScoped<IBaseFeatureInstaller, BaseFeatureInstaller>();
        services.AddScoped<IStoreDataPurger, StoreDataPurger>();
        services.AddScoped<IRetentionPolicyReader, RetentionPolicyReader>();

        // The infrastructure adapter. The manual one produces nothing (an operator,
        // or a real provider adapter, produces the facts); the simulated one
        // produces them all so the self-service journey runs locally. The flag is
        // deliberately off by default — a real deployment must not fabricate
        // infrastructure it does not have (docs/self-service-saas-plan.md §11).
        var simulateInfrastructure = configuration.GetValue<bool>("Provisioning:SimulateInfrastructure");
        if (simulateInfrastructure)
        {
            services.AddScoped<IInfrastructureAdapter, Adapters.SimulatedInfrastructureAdapter>();
        }
        else
        {
            services.AddScoped<IInfrastructureAdapter, Adapters.ManualInfrastructureAdapter>();
        }

        // Both of these are joins between the registry and delivery, which are
        // not allowed to know about each other, so they live here.
        services.AddScoped<IStoreDeliveryReader, StoreDeliveryReader>();
        services.AddScoped<FeatureDelivery.IFeatureVersionReader, FeatureVersionReader>();

        // Where a Feature's service lives, read out of the manifest the store
        // took delivery of, and the call that tells that service who its stores
        // are (docs/adr/0034-a-shared-secret-has-a-lifetime.md).
        services.AddScoped<IServiceEndpointReader, ServiceEndpointReader>();
        services.AddScoped<IFeatureConfigurationContractReader, FeatureConfigurationContractReader>();
        services.AddScoped<IServiceControlPlane, ServiceControlPlaneClient>();

        services.AddOptions<ServiceControlPlaneOptions>()
            .Bind(configuration.GetSection(ServiceControlPlaneOptions.SectionName));

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
        services.AddScoped<ISubscriptionPeriodWriter, SubscriptionPeriodWriter>();
        services.AddScoped<IStoreHostingReader, StoreHostingReader>();
        services.AddScoped<ICustomerStatusReader, CustomerStatusReader>();
        // The entitlement set is read by every store on a timer and changes only
        // when a subscription does, so it is cached behind a short TTL and
        // dropped the moment an entitlement changes. Registered as a decorator so
        // no caller knows the cache is there — including the ingest endpoint,
        // which must keep signing a freshly stamped payload per request even when
        // the set behind it came from the cache.
        services.AddScoped<CustomerEntitlementReader>();
        services.AddScoped<ICustomerEntitlementReader>(provider =>
            new CachingCustomerEntitlementReader(
                provider.GetRequiredService<CustomerEntitlementReader>(),
                provider.GetRequiredService<ICacheService>(),
                provider.GetRequiredService<ILogger<CachingCustomerEntitlementReader>>()));

        // Eviction runs before delivery, so anything delivery triggers reads the
        // new set rather than the one being replaced.
        services.AddScoped<DeliveryEntitlementEventPublisher>();
        services.AddScoped<IEntitlementEventPublisher>(provider =>
            new CompositeEntitlementEventPublisher(
                new EntitlementCacheInvalidator(
                    provider.GetRequiredService<ICacheService>(),
                    provider.GetRequiredService<ILogger<EntitlementCacheInvalidator>>()),
                provider.GetRequiredService<DeliveryEntitlementEventPublisher>()));

        // Everything the store link needs to leave the process: the token it
        // hands out, the key it signs with, and the guarded HTTP client it calls
        // stores on. The address policy and the outbound client are registered
        // together on purpose — there is no way to make a store call that skips
        // the policy.
        services.AddOptions<StoreProbeOptions>()
            .Bind(configuration.GetSection(StoreProbeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Token signing configuration used to be bound by the legacy platform
        // registration; the control plane is the only issuer left, so it binds
        // its own. ValidateOnStart because a host that starts without a signing
        // key only fails at the first sign-in attempt otherwise.
        services.AddOptions<Knight.Infrastructure.Security.JwtOptions>()
            .Bind(configuration.GetSection(Knight.Infrastructure.Security.JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSharedInfrastructureCache(configuration);
        services.AddSingleton<IStoreTokenIssuer, StoreTokenIssuer>();
        services.AddSingleton<IStorePayloadSigner, StorePayloadSigner>();
        services.AddSingleton<IOutboundAddressPolicy, OutboundAddressPolicy>();
        services.AddSingleton<StoreEndpointResolver>();
        services.AddSingleton<IStoreHealthProbe, StoreHealthProbe>();
        services.AddSingleton<IDnsTextResolver, SystemDnsTextResolver>();
        services.AddSingleton<IDomainOwnershipVerifier, DomainOwnershipVerifier>();
        services.AddStoreOutboundHttp();

        // The control plane owns its security primitives outright since phase 8
        // removed the legacy modules they used to adapt over.
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
