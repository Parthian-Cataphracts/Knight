using Identity.Abstractions;
using Identity.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Abstractions.Tenancy;
using Knight.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace Knight.IntegrationTests.Infrastructure;

/// <summary>
/// Spins up a real, ephemeral PostgreSQL instance via Testcontainers and boots the
/// full API host against it, migrated. Requires a running Docker (or
/// Docker-API-compatible) daemon — see backend/README.md's testing
/// section.
///
/// By default, if Docker is unavailable, <see cref="IsAvailable"/> is false and
/// dependent tests skip themselves rather than fail the whole run, so ordinary
/// local development degrades gracefully without container support. Setting the
/// <c>REQUIRE_POSTGRES_TESTS</c> environment variable to <c>1</c>/<c>true</c>
/// switches to mandatory mode: a container start failure is rethrown instead of
/// swallowed, failing the whole suite loudly. Use this for CI and for explicit
/// security-validation runs, where a silently skipped PostgreSQL suite must never
/// be mistaken for a passing one — see docs/adr/0005-postgresql-integration-testing.md.
/// </summary>
public sealed class PostgresApiFixture : IAsyncLifetime
{
    private const string RequireEnvironmentVariable = "REQUIRE_POSTGRES_TESTS";

    private PostgreSqlContainer? _container;

    public bool IsAvailable { get; private set; }

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var required = IsTruthy(Environment.GetEnvironmentVariable(RequireEnvironmentVariable));

        try
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithDatabase("platform_test")
                .WithUsername("platform")
                .WithPassword("platform")
                .Build();

            await _container.StartAsync();
            IsAvailable = true;
        }
        catch when (!required)
        {
            IsAvailable = false;
            return;
        }
        catch (Exception ex) when (required)
        {
            throw new InvalidOperationException(
                $"{RequireEnvironmentVariable} is set, so PostgreSQL-backed integration tests are mandatory, " +
                "but the Testcontainers PostgreSQL container failed to start. Validation cannot proceed with a " +
                "skipped suite. Confirm a Docker (or Docker-API-compatible) daemon is running and reachable.",
                ex);
        }

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Platform", _container.GetConnectionString());
            builder.UseSetting("ConnectionStrings:Redis", "localhost:6379");
            builder.UseSetting("Jwt:SigningKey", "integration-test-signing-key-at-least-32-characters-long");
            builder.UseSetting("Jwt:Issuer", "platform-api");
            builder.UseSetting("Jwt:Audience", "platform-clients");

            // Every test in this collection shares one WebApplicationFactory and
            // therefore one apparent client IP (TestServer has no real network
            // identity), so the per-IP auth rate limits would otherwise collapse
            // unrelated tests into one shared bucket. Raise them here; a
            // dedicated low-limit variant is built specifically to test 429
            // behavior — see LoginRateLimitTests.
            builder.UseSetting("RateLimiting:PlatformLoginPermitLimit", "10000");
            builder.UseSetting("RateLimiting:TenantLoginPermitLimit", "10000");
            builder.UseSetting("RateLimiting:RefreshPermitLimit", "10000");

            // Same reasoning for the two shared non-auth policies: the whole
            // collection looks like a single client to the fixed-window limiters,
            // so the production defaults (100/300 per minute) would otherwise make
            // unrelated tests fail with 429 purely because of suite size.
            builder.UseSetting("RateLimiting:PlatformPermitLimit", "1000000");
            builder.UseSetting("RateLimiting:TenantPublicPermitLimit", "1000000");
            builder.UseSetting("RateLimiting:CheckoutQuotePermitLimit", "1000000");
            builder.UseSetting("RateLimiting:CheckoutSubmitPermitLimit", "1000000");
        });

        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> inside a fresh DI scope with the tenant
    /// context explicitly set (or left empty/platform), giving tests direct,
    /// low-level access to a correctly-scoped <see cref="PlatformDbContext"/> —
    /// the same shape production code uses per request.
    /// </summary>
    public async Task<TResult> WithScopeAsync<TResult>(
        Func<PlatformDbContext, IServiceProvider, Task<TResult>> action,
        Guid? tenantId = null,
        bool platformContext = false)
    {
        using var scope = Factory.Services.CreateScope();
        var accessor = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();

        if (platformContext)
        {
            accessor.SetPlatformContext();
        }
        else if (tenantId.HasValue)
        {
            accessor.SetTenant(tenantId.Value);
        }

        var context = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await action(context, scope.ServiceProvider);
    }

    public Task WithScopeAsync(Func<PlatformDbContext, IServiceProvider, Task> action, Guid? tenantId = null, bool platformContext = false) =>
        WithScopeAsync(async (context, sp) =>
        {
            await action(context, sp);
            return true;
        }, tenantId, platformContext);

    public string CreatePlatformAdminToken(string email = "admin@platform.internal")
    {
        using var scope = Factory.Services.CreateScope();
        var tokenGenerator = scope.ServiceProvider.GetRequiredService<IAccessTokenGenerator>();
        var admin = PlatformAdmin.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, email, "unused-hash", "Test Admin");

        return tokenGenerator.GenerateForPlatformAdmin(admin).Token;
    }

    public string CreateTenantUserToken(Guid tenantId, Guid? userId = null, string email = "user@tenant.internal", IReadOnlyCollection<string>? permissions = null)
    {
        using var scope = Factory.Services.CreateScope();
        var tokenGenerator = scope.ServiceProvider.GetRequiredService<IAccessTokenGenerator>();
        var user = TenantUser.Create(userId ?? Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, email, "unused-hash", "Test User");

        return tokenGenerator.GenerateForTenantUser(user, permissions ?? []).Token;
    }

    /// <summary>Creates and persists an active PlatformAdmin with a real password hash, ready to log in.</summary>
    public async Task<Guid> SeedPlatformAdminAsync(string email, string password, bool activate = true)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await WithScopeAsync(async (context, sp) =>
        {
            var hasher = sp.GetRequiredService<IPasswordHasher>();
            var admin = PlatformAdmin.Create(id, now, email, hasher.Hash(password), "Test Admin");
            if (activate)
            {
                admin.Activate(now);
            }

            await context.PlatformAdmins.AddAsync(admin);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return id;
    }

    /// <summary>Creates and persists an active tenant with a primary domain and one active tenant user.</summary>
    public async Task<(Guid TenantId, string Host, Guid UserId)> SeedActiveTenantWithUserAsync(string email, string password, bool activateUser = true)
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var host = string.Empty;

        await WithScopeAsync(async (context, sp) =>
        {
            var tenant = Tenancy.Domain.Tenant.Create(tenantId, now, $"Tenant {suffix}", $"tenant-{suffix}", "UTC", "USD");
            tenant.Activate(now);
            var domain = tenant.AddDomain(Guid.NewGuid(), $"tenant-{suffix}.example.test", Tenancy.Domain.TenantDomainType.Primary, makePrimary: true, now);
            host = domain.Host;
            await context.Tenants.AddAsync(tenant);

            var hasher = sp.GetRequiredService<IPasswordHasher>();
            var user = TenantUser.Create(userId, now, tenantId, email, hasher.Hash(password), "Test User");
            if (activateUser)
            {
                user.Activate(now);
            }

            await context.TenantUsers.AddAsync(user);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return (tenantId, host, userId);
    }

    public Task SetTenantStatusAsync(Guid tenantId, Action<Tenancy.Domain.Tenant> mutate) =>
        WithScopeAsync(async (context, _) =>
        {
            var tenant = await context.Tenants.FirstAsync(t => t.Id == tenantId);
            mutate(tenant);
            await context.SaveChangesAsync();
        }, platformContext: true);

    /// <summary>Creates and persists a tenant role with the given permission grants, bypassing delegation checks (direct persistence, not the application service).</summary>
    public async Task<Guid> SeedRoleAsync(Guid tenantId, string name, params string[] permissionKeys)
    {
        var roleId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await WithScopeAsync(async (context, _) =>
        {
            var role = Role.Create(roleId, now, tenantId, name);
            await context.Roles.AddAsync(role);

            foreach (var key in permissionKeys)
            {
                await context.RolePermissions.AddAsync(RolePermission.Create(Guid.NewGuid(), tenantId, roleId, key, now));
            }

            await context.SaveChangesAsync();
        }, platformContext: true);

        return roleId;
    }

    public Task AssignRoleAsync(Guid tenantId, Guid userId, Guid roleId) =>
        WithScopeAsync(async (context, _) =>
        {
            await context.TenantUserRoles.AddAsync(TenantUserRole.Create(Guid.NewGuid(), tenantId, userId, roleId, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }, platformContext: true);

    /// <summary>
    /// Ensures the platform-wide <c>catalog</c> feature definition exists. Safe to
    /// call from any test: the definition is global and uniquely keyed, so it is
    /// created once and reused by every subsequent caller.
    /// </summary>
    public Task SeedCatalogFeatureDefinitionAsync() =>
        WithScopeAsync(async (context, _) =>
        {
            var exists = await context.FeatureDefinitions.AnyAsync(f => f.Key == global::Catalog.CatalogFeature.Key);
            if (exists)
            {
                return;
            }

            var definition = FeatureManagement.Domain.FeatureDefinition.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                global::Catalog.CatalogFeature.Key,
                "Catalog",
                "Tenant product catalog.");

            await context.FeatureDefinitions.AddAsync(definition);

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Another test in the shared collection created it first; the
                // global unique key on Key makes this benign.
            }
        }, platformContext: true);

    /// <summary>Enables (or disables) the catalog feature for one tenant.</summary>
    public async Task SetCatalogFeatureAsync(Guid tenantId, bool isEnabled)
    {
        await SeedCatalogFeatureDefinitionAsync();

        await WithScopeAsync(async (context, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await context.TenantFeatures
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == global::Catalog.CatalogFeature.Key);

            if (existing is null)
            {
                await context.TenantFeatures.AddAsync(
                    FeatureManagement.Domain.TenantFeature.Create(Guid.NewGuid(), tenantId, global::Catalog.CatalogFeature.Key, isEnabled, now));
            }
            else if (isEnabled)
            {
                existing.Enable(now);
            }
            else
            {
                existing.Disable(now);
            }

            await context.SaveChangesAsync();
        }, platformContext: true);
    }

    /// <summary>
    /// Seeds an active tenant plus one active user, optionally enabling the
    /// catalog feature, and returns a bearer token carrying
    /// <paramref name="permissions"/>. Feature and permission are set
    /// independently on purpose — the catalog gate requires both, and the tests
    /// prove each one denies on its own.
    /// </summary>
    public async Task<CatalogTenantContext> SeedCatalogTenantAsync(
        bool featureEnabled = true,
        params string[] permissions)
    {
        var (tenantId, host, userId) = await SeedActiveTenantWithUserAsync(
            $"catalog-{Guid.NewGuid():n}@example.test",
            "Password1");

        if (featureEnabled)
        {
            await SetCatalogFeatureAsync(tenantId, isEnabled: true);
        }
        else
        {
            await SeedCatalogFeatureDefinitionAsync();
        }

        var token = CreateTenantUserToken(tenantId, permissions: permissions);

        return new CatalogTenantContext(tenantId, host, userId, token);
    }

    /// <summary>Every catalog permission key, for tests that are not about permission granularity.</summary>
    public static string[] AllCatalogPermissions() =>
        global::Catalog.CatalogPermissions.All.Select(p => p.Key).ToArray();

    /// <summary>Persists a category directly, bypassing the API and its permission checks.</summary>
    public async Task<Guid> SeedCategoryAsync(Guid tenantId, string name, string? slug = null, bool isVisible = true)
    {
        var id = Guid.NewGuid();

        await WithScopeAsync(async (context, _) =>
        {
            var category = global::Catalog.Domain.Category.Create(
                id, DateTimeOffset.UtcNow, tenantId, name, slug, null, isVisible, 0);
            await context.Categories.AddAsync(category);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return id;
    }

    /// <summary>Persists a product directly, bypassing the API and its permission checks.</summary>
    public async Task<Guid> SeedProductAsync(
        Guid tenantId,
        Guid categoryId,
        string name,
        string? slug = null,
        global::Catalog.Domain.ProductStatus status = global::Catalog.Domain.ProductStatus.Active,
        decimal basePrice = 10m,
        bool isVisible = true,
        bool isAvailable = true)
    {
        var id = Guid.NewGuid();

        await WithScopeAsync(async (context, _) =>
        {
            var product = global::Catalog.Domain.Product.Create(
                id, DateTimeOffset.UtcNow, tenantId, categoryId, name, slug, null, status, basePrice, isVisible, isAvailable, 0);
            await context.Products.AddAsync(product);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return id;
    }

    /// <summary>Persists a modifier group directly, bypassing the API and its permission checks.</summary>
    public async Task<Guid> SeedModifierGroupAsync(Guid tenantId, string name)
    {
        var id = Guid.NewGuid();

        await WithScopeAsync(async (context, _) =>
        {
            var group = global::Catalog.Domain.ModifierGroup.Create(
                id, DateTimeOffset.UtcNow, tenantId, name, isRequired: false, minSelections: 0, maxSelections: 3, displayOrder: 0);
            await context.ModifierGroups.AddAsync(group);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return id;
    }

    /// <summary>
    /// Ensures the platform-wide <c>ordering</c> feature definition exists.
    /// </summary>
    public Task SeedOrderingFeatureDefinitionAsync() =>
        WithScopeAsync(async (context, _) =>
        {
            var exists = await context.FeatureDefinitions.AnyAsync(f => f.Key == global::Ordering.OrderingFeature.Key);
            if (exists)
            {
                return;
            }

            var definition = FeatureManagement.Domain.FeatureDefinition.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                global::Ordering.OrderingFeature.Key,
                "Ordering",
                "Tenant ordering and order management.");

            await context.FeatureDefinitions.AddAsync(definition);

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Benign concurrency duplicate
            }
        }, platformContext: true);

    /// <summary>Enables (or disables) the ordering feature for one tenant.</summary>
    public async Task SetOrderingFeatureAsync(Guid tenantId, bool isEnabled)
    {
        await SeedOrderingFeatureDefinitionAsync();

        await WithScopeAsync(async (context, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await context.TenantFeatures
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == global::Ordering.OrderingFeature.Key);

            if (existing is null)
            {
                await context.TenantFeatures.AddAsync(
                    FeatureManagement.Domain.TenantFeature.Create(Guid.NewGuid(), tenantId, global::Ordering.OrderingFeature.Key, isEnabled, now));
            }
            else if (isEnabled)
            {
                existing.Enable(now);
            }
            else
            {
                existing.Disable(now);
            }

            await context.SaveChangesAsync();
        }, platformContext: true);
    }

    /// <summary>
    /// Seeds an active tenant plus one active user with ordering & catalog features optionally enabled,
    /// returning a ready-to-use tenant context with bearer token.
    /// </summary>
    public async Task<OrderingTenantContext> SeedOrderingTenantAsync(
        bool orderingFeatureEnabled = true,
        bool catalogFeatureEnabled = true,
        string[]? permissions = null)
    {
        var (tenantId, host, userId) = await SeedActiveTenantWithUserAsync(
            $"ordering-{Guid.NewGuid():n}@example.test",
            "Password1");

        if (orderingFeatureEnabled)
        {
            await SetOrderingFeatureAsync(tenantId, isEnabled: true);
        }
        else
        {
            await SeedOrderingFeatureDefinitionAsync();
        }

        if (catalogFeatureEnabled)
        {
            await SetCatalogFeatureAsync(tenantId, isEnabled: true);
        }
        else
        {
            await SeedCatalogFeatureDefinitionAsync();
        }

        var effectivePermissions = permissions ?? AllOrderingPermissions();
        var token = CreateTenantUserToken(tenantId, permissions: effectivePermissions);

        return new OrderingTenantContext(tenantId, host, userId, token);
    }

    /// <summary>Every ordering permission key.</summary>
    public static string[] AllOrderingPermissions() =>
        global::Ordering.OrderingPermissions.All.Select(p => p.Key).ToArray();

    public Task SeedCustomerFeatureDefinitionAsync() =>
        WithScopeAsync(async (context, _) =>
        {
            var exists = await context.FeatureDefinitions.AnyAsync(f => f.Key == global::Customer.CustomerFeature.Key);
            if (exists)
            {
                return;
            }

            var definition = FeatureManagement.Domain.FeatureDefinition.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                global::Customer.CustomerFeature.Key,
                "Customers",
                "Tenant customer management and CRM.");

            await context.FeatureDefinitions.AddAsync(definition);

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Benign concurrency duplicate
            }
        }, platformContext: true);

    public async Task SetCustomerFeatureAsync(Guid tenantId, bool isEnabled)
    {
        await SeedCustomerFeatureDefinitionAsync();

        await WithScopeAsync(async (context, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await context.TenantFeatures
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == global::Customer.CustomerFeature.Key);

            if (existing is null)
            {
                await context.TenantFeatures.AddAsync(
                    FeatureManagement.Domain.TenantFeature.Create(Guid.NewGuid(), tenantId, global::Customer.CustomerFeature.Key, isEnabled, now));
            }
            else if (isEnabled)
            {
                existing.Enable(now);
            }
            else
            {
                existing.Disable(now);
            }

            await context.SaveChangesAsync();
        }, platformContext: true);
    }

    public async Task<CustomerTenantContext> SeedCustomerTenantAsync(
        bool customerFeatureEnabled = true,
        string[]? permissions = null)
    {
        var (tenantId, host, userId) = await SeedActiveTenantWithUserAsync(
            $"customer-{Guid.NewGuid():n}@example.test",
            "Password1");

        if (customerFeatureEnabled)
        {
            await SetCustomerFeatureAsync(tenantId, isEnabled: true);
        }
        else
        {
            await SeedCustomerFeatureDefinitionAsync();
        }

        var effectivePermissions = permissions ?? AllCustomerPermissions();
        var token = CreateTenantUserToken(tenantId, permissions: effectivePermissions);

        return new CustomerTenantContext(tenantId, host, userId, token);
    }

    /// <summary>Every customer permission key.</summary>
    public static string[] AllCustomerPermissions() =>
        global::Customer.CustomerPermissions.All.Select(p => p.Key).ToArray();

    public Task SeedDeliveryFeatureDefinitionAsync() =>
        WithScopeAsync(async (context, _) =>
        {
            var exists = await context.FeatureDefinitions.AnyAsync(f => f.Key == global::Delivery.DeliveryFeature.Key);
            if (exists)
            {
                return;
            }

            var definition = FeatureManagement.Domain.FeatureDefinition.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                global::Delivery.DeliveryFeature.Key,
                "Delivery",
                "Tenant delivery zones, settings, and fee calculation.");

            await context.FeatureDefinitions.AddAsync(definition);

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Benign concurrency duplicate
            }
        }, platformContext: true);

    public async Task SetDeliveryFeatureAsync(Guid tenantId, bool isEnabled)
    {
        await SeedDeliveryFeatureDefinitionAsync();

        await WithScopeAsync(async (context, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await context.TenantFeatures
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == global::Delivery.DeliveryFeature.Key);

            if (existing is null)
            {
                await context.TenantFeatures.AddAsync(
                    FeatureManagement.Domain.TenantFeature.Create(Guid.NewGuid(), tenantId, global::Delivery.DeliveryFeature.Key, isEnabled, now));
            }
            else if (isEnabled)
            {
                existing.Enable(now);
            }
            else
            {
                existing.Disable(now);
            }

            await context.SaveChangesAsync();
        }, platformContext: true);
    }

    public async Task<DeliveryTenantContext> SeedDeliveryTenantAsync(
        bool deliveryFeatureEnabled = true,
        string[]? permissions = null)
    {
        var (tenantId, host, userId) = await SeedActiveTenantWithUserAsync(
            $"delivery-{Guid.NewGuid():n}@example.test",
            "Password1");

        if (deliveryFeatureEnabled)
        {
            await SetDeliveryFeatureAsync(tenantId, isEnabled: true);
        }
        else
        {
            await SeedDeliveryFeatureDefinitionAsync();
        }

        var effectivePermissions = permissions ?? AllDeliveryPermissions();
        var token = CreateTenantUserToken(tenantId, permissions: effectivePermissions);

        return new DeliveryTenantContext(tenantId, host, userId, token);
    }

    public async Task<global::Delivery.Domain.DeliveryZone> SeedDeliveryZoneAsync(
        Guid tenantId,
        string name = "Downtown",
        decimal fee = 5.00m,
        decimal? minimumOrderSubtotal = null,
        int displayOrder = 1)
    {
        global::Delivery.Domain.DeliveryZone zone = null!;
        await WithScopeAsync(async (context, _) =>
        {
            zone = global::Delivery.Domain.DeliveryZone.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                tenantId,
                name,
                fee,
                minimumOrderSubtotal,
                displayOrder);

            await context.DeliveryZones.AddAsync(zone);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return zone;
    }

    public async Task SeedFulfillmentSettingsAsync(Guid tenantId, bool pickupEnabled)
    {
        await WithScopeAsync(async (context, _) =>
        {
            var existing = await context.TenantFulfillmentSettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
            if (existing is null)
            {
                await context.TenantFulfillmentSettings.AddAsync(
                    global::Fulfillment.Domain.TenantFulfillmentSettings.Create(tenantId, DateTimeOffset.UtcNow, pickupEnabled));
            }
            else
            {
                existing.Update(pickupEnabled, DateTimeOffset.UtcNow);
            }

            await context.SaveChangesAsync();
        }, platformContext: true);
    }

    public async Task SeedDeliverySettingsAsync(Guid tenantId, bool isAcceptingDeliveryOrders, decimal? defaultMinimumOrderSubtotal = null)
    {
        await WithScopeAsync(async (context, _) =>
        {
            var existing = await context.TenantDeliverySettings.FirstOrDefaultAsync(s => s.TenantId == tenantId);
            if (existing is null)
            {
                await context.TenantDeliverySettings.AddAsync(
                    global::Delivery.Domain.TenantDeliverySettings.Create(tenantId, DateTimeOffset.UtcNow, isAcceptingDeliveryOrders, defaultMinimumOrderSubtotal));
            }
            else
            {
                existing.Update(isAcceptingDeliveryOrders, defaultMinimumOrderSubtotal, DateTimeOffset.UtcNow);
            }

            await context.SaveChangesAsync();
        }, platformContext: true);
    }

    /// <summary>Every delivery permission key.</summary>
    public static string[] AllDeliveryPermissions() =>
        global::Delivery.DeliveryPermissions.All.Select(p => p.Key).ToArray();

    /// <summary>Every fulfillment permission key.</summary>
    public static string[] AllFulfillmentPermissions() =>
        global::Fulfillment.FulfillmentPermissions.All.Select(p => p.Key).ToArray();

    /// <summary>Every payment permission key.</summary>
    public static string[] AllPaymentPermissions() =>
        global::Payment.PaymentPermissions.All.Select(p => p.Key).ToArray();

    public Task SeedPaymentFeatureDefinitionAsync() =>
        WithScopeAsync(async (context, _) =>
        {
            var exists = await context.FeatureDefinitions.AnyAsync(f => f.Key == global::Payment.PaymentFeature.Key);
            if (exists)
            {
                return;
            }

            var definition = FeatureManagement.Domain.FeatureDefinition.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                global::Payment.PaymentFeature.Key,
                "Payment",
                "Tenant payment processing.");

            await context.FeatureDefinitions.AddAsync(definition);

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
            }
        }, platformContext: true);

    public async Task SetPaymentFeatureAsync(Guid tenantId, bool isEnabled)
    {
        await SeedPaymentFeatureDefinitionAsync();
        await WithScopeAsync(async (context, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await context.TenantFeatures
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == global::Payment.PaymentFeature.Key);

            if (existing is null)
            {
                await context.TenantFeatures.AddAsync(
                    FeatureManagement.Domain.TenantFeature.Create(Guid.NewGuid(), tenantId, global::Payment.PaymentFeature.Key, isEnabled, now));
            }
            else if (isEnabled)
            {
                existing.Enable(now);
            }
            else
            {
                existing.Disable(now);
            }

            await context.SaveChangesAsync();
        }, platformContext: true);
    }

    public async Task<PaymentTenantContext> SeedPaymentTenantAsync(
        bool paymentFeatureEnabled = true,
        string[]? permissions = null)
    {
        var (tenantId, host, userId) = await SeedActiveTenantWithUserAsync(
            $"payment-{Guid.NewGuid():n}@example.test",
            "Password1");

        if (paymentFeatureEnabled)
        {
            await SetPaymentFeatureAsync(tenantId, isEnabled: true);
        }
        else
        {
            await SeedPaymentFeatureDefinitionAsync();
        }

        var effectivePermissions = permissions ?? AllPaymentPermissions();
        var token = CreateTenantUserToken(tenantId, userId, permissions: effectivePermissions);

        return new PaymentTenantContext(tenantId, host, userId, token);
    }

    public Task SeedPromotionsFeatureDefinitionAsync() =>
        WithScopeAsync(async (context, _) =>
        {
            var exists = await context.FeatureDefinitions.AnyAsync(f => f.Key == global::Promotions.PromotionsFeature.Key);
            if (exists)
            {
                return;
            }

            var definition = FeatureManagement.Domain.FeatureDefinition.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                global::Promotions.PromotionsFeature.Key,
                "Promotions",
                "Tenant promotions and coupons engine.");

            await context.FeatureDefinitions.AddAsync(definition);

            try
            {
                await context.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
            }
        }, platformContext: true);

    public async Task SetPromotionsFeatureAsync(Guid tenantId, bool isEnabled)
    {
        await SeedPromotionsFeatureDefinitionAsync();
        await WithScopeAsync(async (context, _) =>
        {
            var now = DateTimeOffset.UtcNow;
            var existing = await context.TenantFeatures
                .FirstOrDefaultAsync(f => f.TenantId == tenantId && f.FeatureKey == global::Promotions.PromotionsFeature.Key);

            if (existing is null)
            {
                await context.TenantFeatures.AddAsync(
                    FeatureManagement.Domain.TenantFeature.Create(Guid.NewGuid(), tenantId, global::Promotions.PromotionsFeature.Key, isEnabled, now));
            }
            else if (isEnabled)
            {
                existing.Enable(now);
            }
            else
            {
                existing.Disable(now);
            }

            await context.SaveChangesAsync();
        }, platformContext: true);
    }

    public async Task<PromotionsTenantContext> SeedPromotionsTenantAsync(
        bool promotionsFeatureEnabled = true,
        string[]? permissions = null)
    {
        var (tenantId, host, userId) = await SeedActiveTenantWithUserAsync(
            $"promotions-{Guid.NewGuid():n}@example.test",
            "Password1");

        if (promotionsFeatureEnabled)
        {
            await SetPromotionsFeatureAsync(tenantId, isEnabled: true);
        }
        else
        {
            await SeedPromotionsFeatureDefinitionAsync();
        }

        var effectivePermissions = permissions ?? AllPromotionsAndCouponsPermissions();
        var token = CreateTenantUserToken(tenantId, userId, permissions: effectivePermissions);

        return new PromotionsTenantContext(tenantId, host, userId, token);
    }

    public static string[] AllPromotionsAndCouponsPermissions() =>
        global::Promotions.PromotionsPermissions.All.Select(p => p.Key).ToArray();

    public async Task<Guid> SeedPromotionAsync(
        Guid tenantId,
        string name = "Test Promotion",
        global::Promotions.Domain.PromotionDiscountType discountType = global::Promotions.Domain.PromotionDiscountType.Percentage,
        decimal discountValue = 10m,
        decimal? minSubtotal = null,
        decimal? maxDiscountAmount = null,
        bool requiresCoupon = false,
        int priority = 0,
        bool active = true)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var promo = global::Promotions.Domain.Promotion.Create(
            id,
            tenantId,
            name,
            null,
            discountType,
            discountValue,
            minSubtotal,
            maxDiscountAmount,
            null,
            null,
            requiresCoupon,
            priority,
            now);

        if (active)
        {
            promo.Activate(now);
        }

        await WithScopeAsync(async (context, _) =>
        {
            await context.Promotions.AddAsync(promo);
            await context.SaveChangesAsync();
        }, tenantId: tenantId);

        return id;
    }

    public async Task<Guid> SeedCouponAsync(
        Guid tenantId,
        Guid promotionId,
        string code = "SAVE10",
        int? usageLimitTotal = null)
    {
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var coupon = global::Promotions.Domain.Coupon.Create(
            id,
            tenantId,
            promotionId,
            code,
            usageLimitTotal,
            null,
            null,
            now);

        await WithScopeAsync(async (context, _) =>
        {
            await context.Coupons.AddAsync(coupon);
            await context.SaveChangesAsync();
        }, tenantId: tenantId);

        return id;
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value.Equals("1", StringComparison.Ordinal) || value.Equals("true", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A seeded tenant plus a ready-to-use bearer token, for catalog tests.</summary>
public sealed record CatalogTenantContext(Guid TenantId, string Host, Guid UserId, string Token);

/// <summary>A seeded tenant plus a ready-to-use bearer token, for ordering tests.</summary>
public sealed record OrderingTenantContext(Guid TenantId, string Host, Guid UserId, string Token);

/// <summary>A seeded tenant plus a ready-to-use bearer token, for customer tests.</summary>
public sealed record CustomerTenantContext(Guid TenantId, string Host, Guid UserId, string Token);

/// <summary>A seeded tenant plus a ready-to-use bearer token, for delivery tests.</summary>
public sealed record DeliveryTenantContext(Guid TenantId, string Host, Guid UserId, string Token);

/// <summary>A seeded tenant plus a ready-to-use bearer token, for payment tests.</summary>
public sealed record PaymentTenantContext(Guid TenantId, string Host, Guid UserId, string Token);

/// <summary>A seeded tenant plus a ready-to-use bearer token, for promotions tests.</summary>
public sealed record PromotionsTenantContext(Guid TenantId, string Host, Guid UserId, string Token);

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresApiFixture>
{
    public const string Name = "Postgres";
}
