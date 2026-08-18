using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Knight.Contracts.AccessControl;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.AccessControl;

/// <summary>
/// Mandatory Phase 03 endpoint-authorization boundary tests — see section 92
/// of the phase instructions and docs/architecture/authorization.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthorizationEndpointTests
{
    private readonly PostgresApiFixture _fixture;

    public AuthorizationEndpointTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient CreateClientForHost(string host, string? bearerToken = null)
    {
        var client = _fixture.Factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}");
        if (bearerToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return client;
    }

    [Fact]
    public async Task AnonymousCaller_CannotManageStaff()
    {
        if (!_fixture.IsAvailable) return;

        var (_, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var client = CreateClientForHost(host);

        var response = await client.GetAsync("/api/tenant/staff");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TenantUserWithoutPermission_CannotManageStaff()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var token = _fixture.CreateTenantUserToken(tenantId, permissions: []);
        var client = CreateClientForHost(host, token);

        var response = await client.GetAsync("/api/tenant/staff");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantUserWithCorrectPermission_CanListStaff()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var token = _fixture.CreateTenantUserToken(tenantId, permissions: ["tenant.users.view"]);
        var client = CreateClientForHost(host, token);

        var response = await client.GetAsync("/api/tenant/staff");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_CannotActOnAnotherTenant_ViaHostBoundToOwnTenant()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantAId, hostA, _) = await _fixture.SeedActiveTenantWithUserAsync($"usera-{Guid.NewGuid():n}@example.test", "Password1");
        var (tenantBId, _, userBId) = await _fixture.SeedActiveTenantWithUserAsync($"userb-{Guid.NewGuid():n}@example.test", "Password1");

        // A Tenant A token used against Tenant A's own host cannot reach
        // Tenant B's data — /api/tenant/staff/{id} is always scoped to
        // whichever tenant the token+host resolve to (Tenant A), so
        // Tenant B's user id simply does not exist within that scope.
        var tokenA = _fixture.CreateTenantUserToken(tenantAId, permissions: ["tenant.users.view"]);
        var client = CreateClientForHost(hostA, tokenA);

        var response = await client.GetAsync($"/api/tenant/staff/{userBId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_CannotInspectAnotherTenantsRole_ViaOwnHostBoundToken()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantAId, hostA, _) = await _fixture.SeedActiveTenantWithUserAsync($"usera-{Guid.NewGuid():n}@example.test", "Password1");
        var (tenantBId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"userb-{Guid.NewGuid():n}@example.test", "Password1");
        var roleBId = await _fixture.SeedRoleAsync(tenantBId, "TenantBRole", "tenant.roles.view");

        var tokenA = _fixture.CreateTenantUserToken(tenantAId, permissions: ["tenant.roles.view"]);
        var client = CreateClientForHost(hostA, tokenA);

        var response = await client.GetAsync($"/api/tenant/roles/{roleBId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_CanUsePlatformStaffEndpoints()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var token = _fixture.CreatePlatformAdminToken();
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync($"/api/platform/tenants/{tenantId}/staff");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_CannotUsePlatformEndpoints()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, _) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var token = _fixture.CreateTenantUserToken(tenantId, permissions: ["tenant.users.view"]);
        var client = CreateClientForHost(host, token);

        var response = await client.GetAsync($"/api/platform/tenants/{tenantId}/staff");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
