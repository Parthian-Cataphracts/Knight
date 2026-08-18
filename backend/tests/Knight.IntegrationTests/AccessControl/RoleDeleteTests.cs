using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.AccessControl;

/// <summary>Mandatory Phase 03 role-deletion safety test — see section 91.</summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleDeleteTests
{
    private readonly PostgresApiFixture _fixture;

    public RoleDeleteTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient AdminClient()
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.CreatePlatformAdminToken());
        return client;
    }

    [Fact]
    public async Task DeletingAssignedRole_ReturnsConflict_ThenSucceedsAfterUnassignment()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, userId) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var roleId = await _fixture.SeedRoleAsync(tenantId, "AssignedRole", "tenant.roles.view");
        await _fixture.AssignRoleAsync(tenantId, userId, roleId);

        var client = AdminClient();

        var conflictResponse = await client.DeleteAsync($"/api/platform/tenants/{tenantId}/roles/{roleId}");
        Assert.Equal(HttpStatusCode.Conflict, conflictResponse.StatusCode);

        // Unassign, then deletion must succeed and clean up role_permissions too.
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var assignment = context.TenantUserRoles.Single(a => a.TenantId == tenantId && a.RoleId == roleId);
            context.TenantUserRoles.Remove(assignment);
            await context.SaveChangesAsync();
        }, platformContext: true);

        var deleteResponse = await client.DeleteAsync($"/api/platform/tenants/{tenantId}/roles/{roleId}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        var permissionRowsRemain = await _fixture.WithScopeAsync(
            (context, _) => context.RolePermissions.AnyAsync(rp => rp.TenantId == tenantId && rp.RoleId == roleId),
            platformContext: true);

        Assert.False(permissionRowsRemain);
    }
}
