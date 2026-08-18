using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.IntegrationTests.Infrastructure;
using Tenancy.Domain;
using Xunit;

namespace Knight.IntegrationTests.Security;

/// <summary>
/// Proves the important uniqueness invariants are enforced by real PostgreSQL
/// constraints, not just application-level checks — the last line of defense
/// against race conditions. Inserts bypass the normal application-level
/// "check then insert" services deliberately, to prove the database itself
/// (not just the service) rejects the duplicate.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseConstraintTests
{
    private readonly PostgresApiFixture _fixture;

    public DatabaseConstraintTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task DuplicateTenantSlug_ViolatesUniqueConstraint()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var slug = $"dup-slug-{Guid.NewGuid():n}"[..24];
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var first = Tenant.Create(Guid.NewGuid(), now, "First", slug, "UTC", "USD");
            await context.Tenants.AddAsync(first);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var second = Tenant.Create(Guid.NewGuid(), now, "Second", slug, "UTC", "USD");
            await context.Tenants.AddAsync(second);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task DuplicateNormalizedDomainHost_AcrossDifferentTenants_ViolatesUniqueConstraint()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("n")[..8];
        var host = $"dup-host-{suffix}.example.test";
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var tenant = Tenant.Create(Guid.NewGuid(), now, $"Owner {suffix}", $"owner-{suffix}", "UTC", "USD");
            tenant.AddDomain(Guid.NewGuid(), host, TenantDomainType.Primary, makePrimary: true, now);
            await context.Tenants.AddAsync(tenant);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var otherTenant = Tenant.Create(Guid.NewGuid(), now, $"Claimer {suffix}", $"claimer-{suffix}", "UTC", "USD");
            // Uppercase/whitespace variant of the same normalized host — proves
            // normalization happens before the constraint is hit, not just exact
            // string equality.
            otherTenant.AddDomain(Guid.NewGuid(), $"  {host.ToUpperInvariant()}  ", TenantDomainType.Primary, makePrimary: true, now);
            await context.Tenants.AddAsync(otherTenant);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task DuplicatePrimaryDomainOfSameType_OnSameTenant_ViolatesPartialUniqueConstraint()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("n")[..8];
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var tenant = Tenant.Create(tenantId, now, $"Tenant {suffix}", $"tenant-{suffix}", "UTC", "USD");
            await context.Tenants.AddAsync(tenant);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Bypass Tenant.SetPrimaryDomain's in-memory invariant (which would never
        // allow two primary domains to coexist) by constructing TenantDomain rows
        // directly via its internal constructor and inserting both, proving the
        // database-level partial unique index — not just the aggregate — rejects it.
        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var domainA = new TenantDomain(Guid.NewGuid(), tenantId, $"primary-a-{suffix}.example.test", TenantDomainType.Primary, isPrimary: true, now);
            var domainB = new TenantDomain(Guid.NewGuid(), tenantId, $"primary-b-{suffix}.example.test", TenantDomainType.Primary, isPrimary: true, now);
            context.TenantDomains.AddRange(domainA, domainB);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task DuplicateTenantFeatureKey_OnSameTenant_ViolatesUniqueConstraint()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var suffix = Guid.NewGuid().ToString("n")[..8];
        var featureKey = $"dup-feature-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var tenant = Tenant.Create(tenantId, now, $"Tenant {suffix}", $"tenant-{suffix}", "UTC", "USD");
            await context.Tenants.AddAsync(tenant);

            var definition = FeatureManagement.Domain.FeatureDefinition.Create(Guid.NewGuid(), now, featureKey, "Dup Feature", "test");
            await context.FeatureDefinitions.AddAsync(definition);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var feature = FeatureManagement.Domain.TenantFeature.Create(Guid.NewGuid(), tenantId, featureKey, true, now);
            await context.TenantFeatures.AddAsync(feature);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var duplicate = FeatureManagement.Domain.TenantFeature.Create(Guid.NewGuid(), tenantId, featureKey, false, now);
            await context.TenantFeatures.AddAsync(duplicate);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task DuplicateRefreshTokenHash_ViolatesUniqueConstraint()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var tokenHash = $"hash-{Guid.NewGuid():n}";
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var token = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, tokenHash, now, TimeSpan.FromDays(30));
            await context.RefreshTokens.AddAsync(token);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var duplicate = RefreshToken.IssueNewFamily(Guid.NewGuid(), Guid.NewGuid(), SubjectType.PlatformAdmin, null, tokenHash, now, TimeSpan.FromDays(30));
            await context.RefreshTokens.AddAsync(duplicate);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }
}
