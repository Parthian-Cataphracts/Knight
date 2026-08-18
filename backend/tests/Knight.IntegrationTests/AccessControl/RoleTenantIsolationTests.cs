using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.AccessControl;

/// <summary>
/// Mandatory Phase 03 database-level tenant-consistency scenarios — see
/// docs/architecture/multi-tenancy.md ("Cross-tenant foreign-key protection").
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleTenantIsolationTests
{
    private readonly PostgresApiFixture _fixture;

    public RoleTenantIsolationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SameTenantRoleAssignment_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, userId) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var roleId = await _fixture.SeedRoleAsync(tenantId, "Manager", "tenant.roles.view");

        await _fixture.AssignRoleAsync(tenantId, userId, roleId);

        var assigned = await _fixture.WithScopeAsync(
            (context, _) => context.TenantUserRoles.AnyAsync(a => a.TenantId == tenantId && a.TenantUserId == userId && a.RoleId == roleId),
            platformContext: true);

        Assert.True(assigned);
    }

    [Fact]
    public async Task CrossTenantRoleAssignment_IsRejectedByDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantAId, _, userAId) = await _fixture.SeedActiveTenantWithUserAsync($"usera-{Guid.NewGuid():n}@example.test", "Password1");
        var (tenantBId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"userb-{Guid.NewGuid():n}@example.test", "Password1");
        var roleInTenantB = await _fixture.SeedRoleAsync(tenantBId, "Manager", "tenant.roles.view");

        // Bypass the application layer entirely: attempt to insert a row
        // connecting Tenant A's user to Tenant B's role, with TenantId
        // (incorrectly) set to Tenant A. The composite FK on
        // (TenantId, RoleId) -> roles(TenantId, Id) must reject this — the
        // role only exists under TenantId = B.
        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var invalid = TenantUserRole.Create(Guid.NewGuid(), tenantAId, userAId, roleInTenantB, DateTimeOffset.UtcNow);
            await context.TenantUserRoles.AddAsync(invalid);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task DuplicateNormalizedRoleName_WithinSameTenant_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        await _fixture.SeedRoleAsync(tenantId, "Manager");

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var duplicate = Role.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, " manager ");
            await context.Roles.AddAsync(duplicate);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task SameRoleName_AcrossDifferentTenants_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantAId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"usera-{Guid.NewGuid():n}@example.test", "Password1");
        var (tenantBId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"userb-{Guid.NewGuid():n}@example.test", "Password1");

        var roleAId = await _fixture.SeedRoleAsync(tenantAId, "Manager");
        var roleBId = await _fixture.SeedRoleAsync(tenantBId, "Manager");

        Assert.NotEqual(roleAId, roleBId);
    }

    [Fact]
    public async Task DuplicateRolePermission_Fails()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var roleId = await _fixture.SeedRoleAsync(tenantId, "Manager", "tenant.roles.view");

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var duplicate = RolePermission.Create(Guid.NewGuid(), tenantId, roleId, "tenant.roles.view", DateTimeOffset.UtcNow);
            await context.RolePermissions.AddAsync(duplicate);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task SameNormalizedEmail_AcrossDifferentTenants_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        const string sharedEmail = "shared.staff@example.test";
        var (tenantAId, _, _) = await _fixture.SeedActiveTenantWithUserAsync(sharedEmail, "PasswordA1");
        var (tenantBId, _, _) = await _fixture.SeedActiveTenantWithUserAsync(sharedEmail, "PasswordB1");

        Assert.NotEqual(tenantAId, tenantBId);
    }

    [Fact]
    public async Task DuplicateNormalizedEmail_WithinSameTenant_Fails()
    {
        if (!_fixture.IsAvailable) return;

        const string email = "duplicate.staff@example.test";
        var (tenantId, _, _) = await _fixture.SeedActiveTenantWithUserAsync(email, "Password1");

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var duplicate = TenantUser.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, email.ToUpperInvariant(), "hash", "Duplicate");
            await context.TenantUsers.AddAsync(duplicate);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }
}
