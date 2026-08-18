using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.AccessControl;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.AccessControl;

/// <summary>
/// Mandatory Phase 03 "unknown permission" fail-closed scenarios — see
/// sections 27 and 94 of the phase instructions.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class UnknownPermissionTests
{
    private readonly PostgresApiFixture _fixture;

    public UnknownPermissionTests(PostgresApiFixture fixture)
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
    public async Task CreatingRole_WithUnknownPermissionKey_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var client = AdminClient();

        var response = await client.PostAsJsonAsync($"/api/platform/tenants/{tenantId}/roles", new CreateRoleRequest
        {
            Name = "BadRole",
            PermissionKeys = ["this.permission.does.not.exist"]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var existing = await _fixture.WithScopeAsync(
            (context, _) => context.Roles.AnyAsync(r => r.TenantId == tenantId && r.NormalizedName == "BADROLE"),
            platformContext: true);
        Assert.False(existing);
    }

    [Fact]
    public async Task AssigningUnknownPermissionToExistingRole_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var roleId = await _fixture.SeedRoleAsync(tenantId, "RealRole", "tenant.roles.view");
        var client = AdminClient();

        var response = await client.PutAsJsonAsync($"/api/platform/tenants/{tenantId}/roles/{roleId}/permissions", new SetRolePermissionsRequest
        {
            PermissionKeys = ["tenant.roles.view", "this.permission.does.not.exist"]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // The role's permissions must remain unchanged after the rejection.
        var currentKeys = await _fixture.WithScopeAsync(
            (context, _) => context.RolePermissions.Where(rp => rp.TenantId == tenantId && rp.RoleId == roleId).Select(rp => rp.PermissionKey).ToArrayAsync(),
            platformContext: true);

        Assert.Equal(["tenant.roles.view"], currentKeys);
    }

    [Fact]
    public async Task UnregisteredPermissionRequirement_NeverGrantsAccess_EvenToPlatformAdminScopedTenantUserToken()
    {
        if (!_fixture.IsAvailable) return;

        // No TenantUser could ever hold a claim for an unregistered permission
        // key (role-permission assignment itself rejects unknown keys), but
        // this proves the authorization handler fails closed even if a token
        // were somehow minted carrying one.
        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var token = _fixture.CreateTenantUserToken(tenantId, permissions: ["made.up.permission"]);

        var client = _fixture.Factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Requires "tenant.users.view", which this token does not carry.
        var response = await client.GetAsync("/api/tenant/staff");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
