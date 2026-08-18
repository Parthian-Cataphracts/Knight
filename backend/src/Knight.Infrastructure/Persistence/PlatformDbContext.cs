using System.Linq.Expressions;
using System.Reflection;
using Catalog.Domain;
using FeatureManagement.Domain;
using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.Application.Abstractions.Tenancy;
using Knight.Domain.Common;
using Knight.Infrastructure.Auditing;
using Tenancy.Domain;

namespace Knight.Infrastructure.Persistence;

/// <summary>
/// The single EF Core context for the platform. Tenant-owned entities
/// (<see cref="ITenantScoped"/>) automatically receive a global query filter
/// scoped to the current tenant context — see docs/architecture/multi-tenancy.md.
/// Platform Super Admin operations that must legitimately span tenants rely on
/// <see cref="ITenantContext.IsPlatformContext"/> being explicitly set by
/// <c>TenantResolutionMiddleware</c> before any query runs — never on ad hoc
/// <see cref="IgnoreQueryFilters"/> calls scattered through application code.
///
/// The filter closes over the injected, request-scoped <see cref="ITenantContext"/>
/// rather than over any constant tenant value. EF Core caches the compiled model
/// (and therefore the filter expression) once per <see cref="DbContext"/> type, but
/// re-binds instance-member references in that expression to whichever context
/// instance is actually executing a given query — so this remains correct across
/// many requests despite the one-time model build. This context is registered
/// via <c>AddDbContext</c> (never pooled), so every request gets its own instance
/// and there is no risk of a pooled instance's tenant leaking into another request.
/// </summary>
public sealed class PlatformDbContext : DbContext
{
    private readonly ITenantContext _tenantContext;

    public PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    public DbSet<PlatformAdmin> PlatformAdmins => Set<PlatformAdmin>();

    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();

    public DbSet<Role> Roles => Set<Role>();

    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();

    public DbSet<TenantUserRole> TenantUserRoles => Set<TenantUserRole>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<FeatureDefinition> FeatureDefinitions => Set<FeatureDefinition>();

    public DbSet<TenantFeature> TenantFeatures => Set<TenantFeature>();

    public DbSet<AuditLogEntry> AuditLogEntries => Set<AuditLogEntry>();

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();

    public DbSet<ModifierGroup> ModifierGroups => Set<ModifierGroup>();

    public DbSet<Modifier> Modifiers => Set<Modifier>();

    public DbSet<ProductModifierGroup> ProductModifierGroups => Set<ProductModifierGroup>();

    public DbSet<ProductMedia> ProductMedia => Set<ProductMedia>();

    public DbSet<Ordering.Domain.Order> Orders => Set<Ordering.Domain.Order>();

    public DbSet<Ordering.Domain.OrderItem> OrderItems => Set<Ordering.Domain.OrderItem>();

    public DbSet<Ordering.Domain.OrderItemModifier> OrderItemModifiers => Set<Ordering.Domain.OrderItemModifier>();

    public DbSet<Ordering.Domain.OrderStatusHistory> OrderStatusHistories => Set<Ordering.Domain.OrderStatusHistory>();

    public DbSet<Ordering.Domain.TenantOrderCounter> TenantOrderCounters => Set<Ordering.Domain.TenantOrderCounter>();

    public DbSet<Ordering.Domain.OrderPartySnapshot> OrderPartySnapshots => Set<Ordering.Domain.OrderPartySnapshot>();

    public DbSet<Ordering.Domain.OrderFulfillmentSnapshot> OrderFulfillmentSnapshots => Set<Ordering.Domain.OrderFulfillmentSnapshot>();

    public DbSet<Ordering.Domain.OrderPromotionSnapshot> OrderPromotionSnapshots => Set<Ordering.Domain.OrderPromotionSnapshot>();

    public DbSet<Customer.Domain.Customer> Customers => Set<Customer.Domain.Customer>();

    public DbSet<Fulfillment.Domain.TenantFulfillmentSettings> TenantFulfillmentSettings => Set<Fulfillment.Domain.TenantFulfillmentSettings>();

    public DbSet<Delivery.Domain.TenantDeliverySettings> TenantDeliverySettings => Set<Delivery.Domain.TenantDeliverySettings>();

    public DbSet<Delivery.Domain.DeliveryZone> DeliveryZones => Set<Delivery.Domain.DeliveryZone>();

    public DbSet<Checkout.Domain.CheckoutIdempotencyRecord> CheckoutIdempotencyRecords => Set<Checkout.Domain.CheckoutIdempotencyRecord>();

    public DbSet<Payment.Domain.Payment> Payments => Set<Payment.Domain.Payment>();

    public DbSet<Payment.Domain.PaymentAttempt> PaymentAttempts => Set<Payment.Domain.PaymentAttempt>();

    public DbSet<Payment.Domain.PaymentStatusHistory> PaymentStatusHistories => Set<Payment.Domain.PaymentStatusHistory>();

    public DbSet<Promotions.Domain.Promotion> Promotions => Set<Promotions.Domain.Promotion>();

    public DbSet<Promotions.Domain.Coupon> Coupons => Set<Promotions.Domain.Coupon>();

    public DbSet<Promotions.Domain.CouponRedemption> CouponRedemptions => Set<Promotions.Domain.CouponRedemption>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("platform");
        // Restricted to this context's own configurations: the assembly also
        // carries the control-plane mappings, and an unfiltered scan would pull
        // that unrelated model into this one.
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(PlatformDbContext).Assembly,
            type => type.Namespace == typeof(Configurations.TenantConfiguration).Namespace);

        ApplyTenantQueryFilters(modelBuilder);
    }

    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var method = typeof(PlatformDbContext)
                .GetMethod(nameof(BuildTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(entityType.ClrType);

            var filter = method.Invoke(this, null);
            entityType.SetQueryFilter((LambdaExpression)filter!);
        }
    }

    // Invoked reflectively per tenant-scoped entity type from ApplyTenantQueryFilters.
    // No-tenant-context and no-platform-context both fail closed to an empty result
    // set — absence of a resolved tenant must never be read as "all tenants".
    private LambdaExpression BuildTenantFilter<TEntity>() where TEntity : class, ITenantScoped
    {
        Expression<Func<TEntity, bool>> filter = entity =>
            _tenantContext.IsPlatformContext ||
            (_tenantContext.HasTenant && entity.TenantId == _tenantContext.TenantId);

        return filter;
    }

    /// <summary>
    /// <see cref="Ordering.Domain.OrderStatusHistory"/> is append-only: the aggregate
    /// only ever adds a brand-new row (see <c>Order.ApplyTransition</c>), and nothing
    /// can mutate an existing one — every property is private-set and assigned once in
    /// the constructor.
    ///
    /// EF Core does not infer that. When change detection discovers an untracked entity
    /// hanging off a tracked parent's collection navigation, it decides Added vs Modified
    /// from whether the primary key is already set. These entities are created with a
    /// client-generated <see cref="Guid"/> key, so the key *is* set and EF picks
    /// Modified — which would issue an UPDATE against a row that does not exist yet
    /// instead of inserting the new history entry. Re-stating the intent here restores
    /// the append-only semantics.
    ///
    /// Both overloads are intercepted because every other SaveChanges entry point
    /// funnels into one of these two; overriding only the cancellation-token overload
    /// would leave the invariant unenforced for callers that pass
    /// <c>acceptAllChangesOnSuccess</c> explicitly or save synchronously.
    /// </summary>
    private void RestoreAppendOnlyOrderStatusHistory()
    {
        foreach (var entry in ChangeTracker.Entries<Ordering.Domain.OrderStatusHistory>())
        {
            if (entry.State == EntityState.Modified)
            {
                entry.State = EntityState.Added;
            }
        }
    }

    /// <inheritdoc cref="RestoreAppendOnlyOrderStatusHistory" />
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RestoreAppendOnlyOrderStatusHistory();

        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc cref="RestoreAppendOnlyOrderStatusHistory" />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RestoreAppendOnlyOrderStatusHistory();

        return base.SaveChanges(acceptAllChangesOnSuccess);
    }
}
