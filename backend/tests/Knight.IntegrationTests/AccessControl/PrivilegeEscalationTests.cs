using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Knight.Contracts.AccessControl;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.AccessControl;

/// <summary>
/// Mandatory Phase 03 privilege-escalation prevention tests — see
/// docs/architecture/authorization.md ("privilege delegation") and
/// sections 79/80 of the phase instructions.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PrivilegeEscalationTests
{
    private readonly PostgresApiFixture _fixture;

    public PrivilegeEscalationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient CreateClientForHost(string host, string bearerToken)
    {
        var client = _fixture.Factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        return client;
    }

    [Fact]
    public async Task PermissionAssignmentEscalation_CallerCannotGrantPermissionTheyDoNotHave()
    {
        if (!_fixture.IsAvailable) return;

        // Caller has role.view + role.update, but not tenant.users.disable.
        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var targetRoleId = await _fixture.SeedRoleAsync(tenantId, "TargetRole", "tenant.roles.view", "tenant.roles.update");

        var callerToken = _fixture.CreateTenantUserToken(tenantId, permissions: ["tenant.roles.view", "tenant.roles.update", "tenant.roles.permissions.assign"]);
        var client = CreateClientForHost(host, callerToken);

        var response = await client.PutAsJsonAsync($"/api/tenant/roles/{targetRoleId}/permissions", new SetRolePermissionsRequest
        {
            PermissionKeys = ["tenant.roles.view", "tenant.roles.update", "tenant.users.disable"]
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RoleAssignmentEscalation_CallerCannotAssignRoleGrantingPermissionsTheyDoNotHave()
    {
        if (!_fixture.IsAvailable) return;

        // Role permissions = A,B,C; caller effective permissions = A,B only.
        var (tenantId, host, targetUserId) = await _fixture.SeedActiveTenantWithUserAsync($"target-{Guid.NewGuid():n}@example.test", "Password1");
        var roleId = await _fixture.SeedRoleAsync(tenantId, "OverPrivilegedRole", "tenant.roles.view", "tenant.users.view", "tenant.users.disable");

        var callerToken = _fixture.CreateTenantUserToken(tenantId, permissions: ["tenant.roles.view", "tenant.users.view", "tenant.users.roles.assign"]);
        var client = CreateClientForHost(host, callerToken);

        var response = await client.PutAsJsonAsync($"/api/tenant/staff/{targetUserId}/roles", new ReplaceStaffRolesRequest { RoleIds = [roleId] });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_CanPerformEquivalentRoleAssignment_BypassingDelegationCheck()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, targetUserId) = await _fixture.SeedActiveTenantWithUserAsync($"target-{Guid.NewGuid():n}@example.test", "Password1");
        var roleId = await _fixture.SeedRoleAsync(tenantId, "OverPrivilegedRole", "tenant.roles.view", "tenant.users.view", "tenant.users.disable");

        var platformToken = _fixture.CreatePlatformAdminToken();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", platformToken);

        var response = await client.PutAsJsonAsync($"/api/platform/tenants/{tenantId}/staff/{targetUserId}/roles", new ReplaceStaffRolesRequest { RoleIds = [roleId] });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RoleModificationEscalation_AppliesEvenToCallersOwnAssignedRole()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, callerId) = await _fixture.SeedActiveTenantWithUserAsync($"caller-{Guid.NewGuid():n}@example.test", "Password1");
        var ownRoleId = await _fixture.SeedRoleAsync(tenantId, "OwnRole", "tenant.roles.view", "tenant.roles.update", "tenant.roles.permissions.assign");
        await _fixture.AssignRoleAsync(tenantId, callerId, ownRoleId);

        var callerToken = _fixture.CreateTenantUserToken(tenantId, permissions: ["tenant.roles.view", "tenant.roles.update", "tenant.roles.permissions.assign"]);
        var client = CreateClientForHost(host, callerToken);

        var response = await client.PutAsJsonAsync($"/api/tenant/roles/{ownRoleId}/permissions", new SetRolePermissionsRequest
        {
            PermissionKeys = ["tenant.roles.view", "tenant.roles.update", "tenant.roles.permissions.assign", "tenant.users.disable"]
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
