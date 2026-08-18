using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Security;

/// <summary>
/// Proves the EF Core global tenant query filter (Knight.Infrastructure.Persistence.PlatformDbContext)
/// actually isolates tenant-scoped data against a real PostgreSQL database, and
/// that the filter re-evaluates per request/scope rather than freezing to
/// whichever DbContext instance happened to trigger the cached model build —
/// see docs/architecture/multi-tenancy.md. Uses two generic, test-only tenants
/// ("Tenant Alpha" / "Tenant Beta") and the real TenantUser entity as the
/// tenant-scoped subject under test.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantQueryFilterIsolationTests
{
    private readonly PostgresApiFixture _fixture;

    public TenantQueryFilterIsolationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TenantAlpha_CannotReadTenantBetaData()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (alphaId, betaId) = await SeedTwoTenantsWithUsersAsync();

        var visibleToAlpha = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.Select(u => u.Email).ToListAsync(),
            tenantId: alphaId);

        Assert.Single(visibleToAlpha);
        Assert.Contains("alpha-user@example.com", visibleToAlpha);
        Assert.DoesNotContain("beta-user@example.com", visibleToAlpha);
    }

    [Fact]
    public async Task TenantBeta_CannotReadTenantAlphaData()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (alphaId, betaId) = await SeedTwoTenantsWithUsersAsync();

        var visibleToBeta = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.Select(u => u.Email).ToListAsync(),
            tenantId: betaId);

        Assert.Single(visibleToBeta);
        Assert.Contains("beta-user@example.com", visibleToBeta);
        Assert.DoesNotContain("alpha-user@example.com", visibleToBeta);
    }

    [Fact]
    public async Task NoTenantContext_ReturnsNoRows_FailsClosed()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        await SeedTwoTenantsWithUsersAsync();

        var visible = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.ToListAsync());

        Assert.Empty(visible);
    }

    [Fact]
    public async Task PlatformContext_SeesAcrossTenants()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (alphaId, betaId) = await SeedTwoTenantsWithUsersAsync();

        var visible = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.Select(u => u.Email).ToListAsync(),
            platformContext: true);

        Assert.Contains("alpha-user@example.com", visible);
        Assert.Contains("beta-user@example.com", visible);
    }

    [Fact]
    public async Task ScopedToTenantAlpha_CannotLoadOrUpdateTenantBetaEntityById()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (alphaId, betaId) = await SeedTwoTenantsWithUsersAsync();

        var betaUserId = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.Where(u => u.TenantId == betaId).Select(u => u.Id).FirstAsync(),
            platformContext: true);

        var loadedFromAlphaScope = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.FirstOrDefaultAsync(u => u.Id == betaUserId),
            tenantId: alphaId);

        Assert.Null(loadedFromAlphaScope);
    }

    [Fact]
    public async Task ScopedToTenantAlpha_CannotUpdateTenantBetaEntityThroughBulkUpdate()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (alphaId, betaId) = await SeedTwoTenantsWithUsersAsync();

        var betaUserId = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.Where(u => u.TenantId == betaId).Select(u => u.Id).FirstAsync(),
            platformContext: true);

        // ExecuteUpdateAsync goes straight to SQL (no load-then-save), so this
        // also proves the global query filter is applied to bulk operations, not
        // just LINQ-to-objects reads.
        var affectedRows = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers
                .Where(u => u.Id == betaUserId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(u => u.DisplayName, "Tampered By Alpha")),
            tenantId: alphaId);

        Assert.Equal(0, affectedRows);

        var betaDisplayNameUnchanged = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.Where(u => u.Id == betaUserId).Select(u => u.DisplayName).FirstAsync(),
            platformContext: true);

        Assert.Equal("Beta User", betaDisplayNameUnchanged);
    }

    [Fact]
    public async Task ScopedToTenantAlpha_CannotDeleteTenantBetaEntityThroughBulkDelete()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var (alphaId, betaId) = await SeedTwoTenantsWithUsersAsync();

        var betaUserId = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.Where(u => u.TenantId == betaId).Select(u => u.Id).FirstAsync(),
            platformContext: true);

        // ExecuteDeleteAsync goes straight to SQL (no load-then-remove), so this
        // also proves the global query filter is applied to bulk deletes.
        var affectedRows = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.Where(u => u.Id == betaUserId).ExecuteDeleteAsync(),
            tenantId: alphaId);

        Assert.Equal(0, affectedRows);

        var betaUserStillExists = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUsers.AnyAsync(u => u.Id == betaUserId),
            platformContext: true);

        Assert.True(betaUserStillExists, "Tenant Beta's row must survive a delete attempt issued from Tenant Alpha's scope.");
    }

    [Fact]
    public async Task ManyScopesWithDifferentTenants_EachSeeOnlyTheirOwnRows_DespiteSharedCachedModel()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        // Regression coverage for the highest-priority Phase 01 review item: EF
        // Core caches the compiled model (and therefore the filter expression)
        // once per DbContext type. This repeatedly constructs new scopes/contexts
        // with alternating tenants to prove the filter is re-evaluated against
        // each request's own tenant context rather than the tenant that happened
        // to be active when the model was first built.
        var (alphaId, betaId) = await SeedTwoTenantsWithUsersAsync();

        for (var i = 0; i < 5; i++)
        {
            var alphaEmails = await _fixture.WithScopeAsync(
                (context, _) => context.TenantUsers.Select(u => u.Email).ToListAsync(),
                tenantId: alphaId);
            Assert.Equal(["alpha-user@example.com"], alphaEmails);

            var betaEmails = await _fixture.WithScopeAsync(
                (context, _) => context.TenantUsers.Select(u => u.Email).ToListAsync(),
                tenantId: betaId);
            Assert.Equal(["beta-user@example.com"], betaEmails);
        }
    }

    private async Task<(Guid AlphaId, Guid BetaId)> SeedTwoTenantsWithUsersAsync()
    {
        var alphaId = Guid.NewGuid();
        var betaId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("n")[..8];

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var alpha = Tenancy.Domain.Tenant.Create(alphaId, now, $"Tenant Alpha {suffix}", $"tenant-alpha-{suffix}", "UTC", "USD");
            var beta = Tenancy.Domain.Tenant.Create(betaId, now, $"Tenant Beta {suffix}", $"tenant-beta-{suffix}", "UTC", "USD");
            await context.Tenants.AddRangeAsync(alpha, beta);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var user = TenantUser.Create(Guid.NewGuid(), now, alphaId, "alpha-user@example.com", "unused-hash", "Alpha User");
            await context.TenantUsers.AddAsync(user);
            await context.SaveChangesAsync();
        }, tenantId: alphaId);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var user = TenantUser.Create(Guid.NewGuid(), now, betaId, "beta-user@example.com", "unused-hash", "Beta User");
            await context.TenantUsers.AddAsync(user);
            await context.SaveChangesAsync();
        }, tenantId: betaId);

        return (alphaId, betaId);
    }
}
