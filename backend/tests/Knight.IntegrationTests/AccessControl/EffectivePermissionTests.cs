using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Identity;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.AccessControl;

/// <summary>
/// Mandatory Phase 03 effective-permission resolution scenarios — see
/// docs/architecture/authorization.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class EffectivePermissionTests
{
    private readonly PostgresApiFixture _fixture;

    public EffectivePermissionTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient CreateClientForHost(string host)
    {
        var client = _fixture.Factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}");
        return client;
    }

    [Fact]
    public async Task EffectivePermissions_UnionAcrossMultipleRoles_HasNoDuplicates()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, _, userId) = await _fixture.SeedActiveTenantWithUserAsync($"user-{Guid.NewGuid():n}@example.test", "Password1");
        var roleA = await _fixture.SeedRoleAsync(tenantId, "RoleA", "tenant.roles.view", "tenant.users.view");
        var roleB = await _fixture.SeedRoleAsync(tenantId, "RoleB", "tenant.users.view", "tenant.users.create");
        await _fixture.AssignRoleAsync(tenantId, userId, roleA);
        await _fixture.AssignRoleAsync(tenantId, userId, roleB);

        var permissions = await _fixture.WithScopeAsync(
            (_, sp) => sp.GetRequiredService<IEffectivePermissionService>().GetEffectivePermissionKeysAsync(tenantId, userId, CancellationToken.None),
            tenantId: tenantId);

        Assert.Equal(3, permissions.Count);
        Assert.Contains("tenant.roles.view", permissions);
        Assert.Contains("tenant.users.view", permissions);
        Assert.Contains("tenant.users.create", permissions);
    }

    [Fact]
    public async Task Login_EmitsEffectivePermissionClaims()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, userId) = await _fixture.SeedActiveTenantWithUserAsync(email, "Password1");
        var roleId = await _fixture.SeedRoleAsync(tenantId, "Viewer", "tenant.roles.view");
        await _fixture.AssignRoleAsync(tenantId, userId, roleId);

        var client = CreateClientForHost(host);
        var response = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "Password1" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        var token = new JwtSecurityTokenHandler().ReadJwtToken(body!.AccessToken);
        var claimedPermissions = token.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToArray();

        Assert.Contains("tenant.roles.view", claimedPermissions);
    }

    [Fact]
    public async Task Refresh_AfterRoleChange_ReflectsNewPermissions_NotOldOnes()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, userId) = await _fixture.SeedActiveTenantWithUserAsync(email, "Password1");
        var roleId = await _fixture.SeedRoleAsync(tenantId, "RoleA", "tenant.roles.view");
        await _fixture.AssignRoleAsync(tenantId, userId, roleId);

        var client = CreateClientForHost(host);
        var loginResponse = await client.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "Password1" });
        var loginBody = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();
        var loginToken = new JwtSecurityTokenHandler().ReadJwtToken(loginBody!.AccessToken);
        var loginPermissions = loginToken.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToArray();
        Assert.Contains("tenant.roles.view", loginPermissions);
        Assert.DoesNotContain("tenant.users.view", loginPermissions);

        // Change the role's permissions (P1 -> P2) directly, simulating an
        // administrator's edit between the login and the refresh.
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var existing = context.RolePermissions.Where(rp => rp.TenantId == tenantId && rp.RoleId == roleId);
            context.RolePermissions.RemoveRange(existing);
            await context.SaveChangesAsync();

            await context.RolePermissions.AddAsync(Identity.Domain.RolePermission.Create(Guid.NewGuid(), tenantId, roleId, "tenant.users.view", DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }, platformContext: true);

        var refreshResponse = await client.PostAsync("/api/tenant/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);
        var refreshBody = await refreshResponse.Content.ReadFromJsonAsync<LoginResponse>();
        var refreshToken = new JwtSecurityTokenHandler().ReadJwtToken(refreshBody!.AccessToken);
        var refreshPermissions = refreshToken.Claims.Where(c => c.Type == "permission").Select(c => c.Value).ToArray();

        Assert.Contains("tenant.users.view", refreshPermissions);
        Assert.DoesNotContain("tenant.roles.view", refreshPermissions);
    }
}
