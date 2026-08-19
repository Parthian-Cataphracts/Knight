using System.Linq.Expressions;
using System.Reflection;
using AccessControl.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Domain.Common;
using Knight.Infrastructure.ControlPlane.Configurations;
using Microsoft.EntityFrameworkCore;
using Stores.Domain;

// The legacy store-side module is also called Customer; aliasing keeps the two
// unrelated concepts from being confused for one another in this file.
using ControlPlaneCustomer = Customers.Domain.Customer;

namespace Knight.Infrastructure.ControlPlane;

/// <summary>
/// The control plane's own EF Core context, deliberately separate from the
/// legacy <c>PlatformDbContext</c>. KNIGHT manages stores; it is not a store's
/// backend, and the two schemas have nothing to say to each other
/// (docs/README.md, rules 1 and 3). Keeping them apart also means the frozen
/// store-side tables can be dropped in phase 8 without touching a single
/// control-plane migration.
///
/// Every entity implementing <see cref="ICustomerScoped"/> receives a global
/// query filter derived from the request's <see cref="ICustomerScope"/>. The
/// filter is the safety net described in docs/authorization.md section 3: a
/// handler that forgets its own customer check still cannot return another
/// customer's rows. It fails closed — an unresolved scope yields nothing, never
/// everything.
///
/// The filter closes over the injected, request-scoped scope rather than over a
/// constant. EF Core compiles the model once per context type but re-binds
/// instance-member references to whichever instance is executing the query, so
/// this stays correct across requests. The context is registered with
/// AddDbContext (never pooled), so no instance outlives its request.
/// </summary>
public sealed class ControlPlaneDbContext : DbContext
{
    public const string SchemaName = "control";

    private readonly ICustomerScope _scope;

    public ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options, ICustomerScope scope)
        : base(options)
    {
        _scope = scope;
    }

    public DbSet<ControlPlaneCustomer> Customers => Set<ControlPlaneCustomer>();

    public DbSet<Store> Stores => Set<Store>();

    public DbSet<StoreCredential> StoreCredentials => Set<StoreCredential>();

    public DbSet<StoreHealthCheck> StoreHealthChecks => Set<StoreHealthCheck>();

    public DbSet<StoreDeployment> StoreDeployments => Set<StoreDeployment>();

    public DbSet<Ingestion.Domain.StoreErrorEvent> StoreErrorEvents => Set<Ingestion.Domain.StoreErrorEvent>();

    public DbSet<Ingestion.Domain.StoreLifecycleEvent> StoreEvents => Set<Ingestion.Domain.StoreLifecycleEvent>();

    public DbSet<Ingestion.Domain.StoreLogEntry> StoreLogEntries => Set<Ingestion.Domain.StoreLogEntry>();

    public DbSet<ControlPlaneUser> Users => Set<ControlPlaneUser>();

    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<UserSession> UserSessions => Set<UserSession>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<FeatureRegistry.Domain.Feature> Features => Set<FeatureRegistry.Domain.Feature>();

    public DbSet<Plans.Domain.Plan> Plans => Set<Plans.Domain.Plan>();

    public DbSet<Plans.Domain.FeaturePrice> FeaturePrices => Set<Plans.Domain.FeaturePrice>();

    public DbSet<Subscriptions.Domain.Subscription> Subscriptions => Set<Subscriptions.Domain.Subscription>();

    public DbSet<Subscriptions.Domain.FeatureEntitlement> FeatureEntitlements => Set<Subscriptions.Domain.FeatureEntitlement>();

    public DbSet<FeatureRegistry.Domain.FeatureVersion> FeatureVersions => Set<FeatureRegistry.Domain.FeatureVersion>();

    public DbSet<FeatureRegistry.Domain.FeatureDependency> FeatureDependencies => Set<FeatureRegistry.Domain.FeatureDependency>();

    public DbSet<FeatureDelivery.Domain.FeatureInstallation> FeatureInstallations => Set<FeatureDelivery.Domain.FeatureInstallation>();

    public DbSet<FeatureDelivery.Domain.FeatureInstallationJob> FeatureInstallationJobs => Set<FeatureDelivery.Domain.FeatureInstallationJob>();

    public DbSet<FeatureDelivery.Domain.JobStepResult> FeatureJobSteps => Set<FeatureDelivery.Domain.JobStepResult>();

    public DbSet<FeatureDelivery.Domain.FeatureConfiguration> FeatureConfigurations => Set<FeatureDelivery.Domain.FeatureConfiguration>();

    public DbSet<Servers.Domain.Server> Servers => Set<Servers.Domain.Server>();

    public DbSet<Servers.Domain.Agent> Agents => Set<Servers.Domain.Agent>();

    public DbSet<Servers.Domain.ServerMetric> ServerMetrics => Set<Servers.Domain.ServerMetric>();

    public DbSet<Servers.Domain.Alert> Alerts => Set<Servers.Domain.Alert>();

    public DbSet<Billing.Domain.BillingAccount> BillingAccounts => Set<Billing.Domain.BillingAccount>();

    public DbSet<Billing.Domain.Invoice> Invoices => Set<Billing.Domain.Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        // Applied explicitly rather than by assembly scan: this assembly also
        // carries the legacy platform configurations, and the two models must
        // not bleed into each other.
        modelBuilder.ApplyConfiguration(new CustomerConfiguration());
        modelBuilder.ApplyConfiguration(new StoreConfiguration());
        modelBuilder.ApplyConfiguration(new StoreCredentialConfiguration());
        modelBuilder.ApplyConfiguration(new ControlPlaneUserConfiguration());
        modelBuilder.ApplyConfiguration(new UserRoleAssignmentConfiguration());
        modelBuilder.ApplyConfiguration(new RoleConfiguration());
        modelBuilder.ApplyConfiguration(new RolePermissionConfiguration());
        modelBuilder.ApplyConfiguration(new UserSessionConfiguration());
        modelBuilder.ApplyConfiguration(new AuditLogConfiguration());

        modelBuilder.ApplyConfiguration(new StoreHealthCheckConfiguration());
        modelBuilder.ApplyConfiguration(new StoreDeploymentConfiguration());
        modelBuilder.ApplyConfiguration(new StoreErrorEventConfiguration());
        modelBuilder.ApplyConfiguration(new StoreLifecycleEventConfiguration());
        modelBuilder.ApplyConfiguration(new StoreLogEntryConfiguration());

        modelBuilder.ApplyConfiguration(new FeatureConfiguration());
        modelBuilder.ApplyConfiguration(new PlanConfiguration());
        modelBuilder.ApplyConfiguration(new PlanFeatureConfiguration());
        modelBuilder.ApplyConfiguration(new FeaturePriceConfiguration());
        modelBuilder.ApplyConfiguration(new SubscriptionConfiguration());
        modelBuilder.ApplyConfiguration(new SubscriptionFeatureConfiguration());
        modelBuilder.ApplyConfiguration(new FeatureEntitlementConfiguration());
        modelBuilder.ApplyConfiguration(new BillingAccountConfiguration());
        modelBuilder.ApplyConfiguration(new InvoiceConfiguration());
        modelBuilder.ApplyConfiguration(new InvoiceLineConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentRecordConfiguration());
        modelBuilder.ApplyConfiguration(new InvoiceNumberSequenceConfiguration());

        modelBuilder.ApplyConfiguration(new FeatureVersionConfiguration());
        modelBuilder.ApplyConfiguration(new FeatureDependencyConfiguration());
        modelBuilder.ApplyConfiguration(new FeatureInstallationConfiguration());
        modelBuilder.ApplyConfiguration(new FeatureInstallationJobConfiguration());
        modelBuilder.ApplyConfiguration(new JobStepResultConfiguration());
        modelBuilder.ApplyConfiguration(new StoreFeatureConfigurationConfiguration());

        modelBuilder.ApplyConfiguration(new ServerConfiguration());
        modelBuilder.ApplyConfiguration(new AgentConfiguration());
        modelBuilder.ApplyConfiguration(new ServerMetricConfiguration());
        modelBuilder.ApplyConfiguration(new AlertConfiguration());

        ApplyCustomerIsolation(modelBuilder);
    }

    private void ApplyCustomerIsolation(ModelBuilder modelBuilder)
    {
        // The customer aggregate is scoped by its own identity rather than by a
        // CustomerId column, so it gets its filter directly.
        modelBuilder.Entity<ControlPlaneCustomer>().HasQueryFilter(customer =>
            _scope.IsPlatformScope || (_scope.HasCustomer && customer.Id == _scope.CustomerId));

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var builderName = entityType.ClrType switch
            {
                var type when typeof(ICustomerScoped).IsAssignableFrom(type) => nameof(BuildCustomerFilter),
                var type when typeof(ICustomerOwned).IsAssignableFrom(type) => nameof(BuildOwnedFilter),
                _ => null,
            };

            if (builderName is null)
            {
                continue;
            }

            var filter = typeof(ControlPlaneDbContext)
                .GetMethod(builderName, BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, null);

            entityType.SetQueryFilter((LambdaExpression)filter!);
        }
    }

    // Invoked reflectively per customer-scoped entity type. A row with no
    // customer is platform-owned and belongs to platform principals only: "no
    // customer" must never be read as "any customer".
    private LambdaExpression BuildCustomerFilter<TEntity>() where TEntity : class, ICustomerScoped
    {
        Expression<Func<TEntity, bool>> filter = entity =>
            _scope.IsPlatformScope ||
            (_scope.HasCustomer && entity.CustomerId == _scope.CustomerId);

        return filter;
    }

    // The same rule for entities whose customer is mandatory. Written separately
    // rather than shared through a cast because EF Core must see a comparison
    // against the mapped column to translate it into SQL.
    private LambdaExpression BuildOwnedFilter<TEntity>() where TEntity : class, ICustomerOwned
    {
        Expression<Func<TEntity, bool>> filter = entity =>
            _scope.IsPlatformScope ||
            (_scope.HasCustomer && entity.CustomerId == _scope.CustomerId);

        return filter;
    }
}
