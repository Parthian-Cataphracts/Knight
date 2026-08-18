using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Knight.Contracts.Auth;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.AccessControl;

/// <summary>
/// Mandatory Phase 03 staff enable/disable/session-revocation scenarios — see
/// docs/architecture/authorization.md ("account disable is different").
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class StaffLifecycleTests
{
    private readonly PostgresApiFixture _fixture;

    public StaffLifecycleTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient CreateClientForHost(string host)
    {
        var client = _fixture.Factory.CreateClient();
        client.BaseAddress = new Uri($"http://{host}");
        return client;
    }

    private HttpClient AdminClient()
    {
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _fixture.CreatePlatformAdminToken());
        return client;
    }

    [Fact]
    public async Task DisablingStaff_RevokesRefreshSession_RefreshFails_NewLoginFails()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, userId) = await _fixture.SeedActiveTenantWithUserAsync(email, "Password1");

        var staffClient = CreateClientForHost(host);
        await staffClient.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "Password1" });

        var disableResponse = await AdminClient().PostAsync($"/api/platform/tenants/{tenantId}/staff/{userId}/disable", null);
        Assert.Equal(HttpStatusCode.OK, disableResponse.StatusCode);

        var refreshResponse = await staffClient.PostAsync("/api/tenant/auth/refresh", null);
        Assert.NotEqual(HttpStatusCode.OK, refreshResponse.StatusCode);

        var newLoginResponse = await CreateClientForHost(host).PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "Password1" });
        Assert.Equal(HttpStatusCode.Unauthorized, newLoginResponse.StatusCode);
    }

    [Fact]
    public async Task ReEnablingStaff_DoesNotResurrectOldSession_ButAllowsFreshLogin()
    {
        if (!_fixture.IsAvailable) return;

        var email = $"user-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, userId) = await _fixture.SeedActiveTenantWithUserAsync(email, "Password1");

        var oldSessionClient = CreateClientForHost(host);
        await oldSessionClient.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "Password1" });

        await AdminClient().PostAsync($"/api/platform/tenants/{tenantId}/staff/{userId}/disable", null);
        var enableResponse = await AdminClient().PostAsync($"/api/platform/tenants/{tenantId}/staff/{userId}/enable", null);
        Assert.Equal(HttpStatusCode.OK, enableResponse.StatusCode);

        // The old (pre-disable) refresh session must remain unusable.
        var oldRefreshResponse = await oldSessionClient.PostAsync("/api/tenant/auth/refresh", null);
        Assert.NotEqual(HttpStatusCode.OK, oldRefreshResponse.StatusCode);

        // A brand-new login must succeed.
        var newLoginResponse = await CreateClientForHost(host).PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = email, Password = "Password1" });
        Assert.Equal(HttpStatusCode.OK, newLoginResponse.StatusCode);
    }

    [Fact]
    public async Task AdministrativeSessionRevocation_AffectsOnlyTargetUser()
    {
        if (!_fixture.IsAvailable) return;

        var emailA = $"targeta-{Guid.NewGuid():n}@example.test";
        var emailB = $"targetb-{Guid.NewGuid():n}@example.test";
        var (tenantId, host, userAId) = await _fixture.SeedActiveTenantWithUserAsync(emailA, "PasswordA1");

        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var hasher = sp.GetRequiredService<Identity.Abstractions.IPasswordHasher>();
            var userB = Identity.Domain.TenantUser.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, emailB, hasher.Hash("PasswordB1"), "User B");
            userB.Activate(DateTimeOffset.UtcNow);
            await context.TenantUsers.AddAsync(userB);
            await context.SaveChangesAsync();
        }, platformContext: true);

        var clientA = CreateClientForHost(host);
        await clientA.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = emailA, Password = "PasswordA1" });

        var clientB = CreateClientForHost(host);
        await clientB.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = emailB, Password = "PasswordB1" });

        var revokeResponse = await AdminClient().PostAsync($"/api/platform/tenants/{tenantId}/staff/{userAId}/sessions/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var refreshA = await clientA.PostAsync("/api/tenant/auth/refresh", null);
        var refreshB = await clientB.PostAsync("/api/tenant/auth/refresh", null);

        Assert.NotEqual(HttpStatusCode.OK, refreshA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, refreshB.StatusCode);
    }

    [Fact]
    public async Task AdministrativeSessionRevocation_DoesNotAffectCrossTenantUsersSession()
    {
        if (!_fixture.IsAvailable) return;

        var emailA = $"usera-{Guid.NewGuid():n}@example.test";
        var emailOther = $"userother-{Guid.NewGuid():n}@example.test";
        var (tenantAId, hostA, userAId) = await _fixture.SeedActiveTenantWithUserAsync(emailA, "PasswordA1");
        var (_, hostOther, _) = await _fixture.SeedActiveTenantWithUserAsync(emailOther, "PasswordOther1");

        var clientA = CreateClientForHost(hostA);
        await clientA.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = emailA, Password = "PasswordA1" });

        var clientOther = CreateClientForHost(hostOther);
        await clientOther.PostAsJsonAsync("/api/tenant/auth/login", new LoginRequest { Email = emailOther, Password = "PasswordOther1" });

        await AdminClient().PostAsync($"/api/platform/tenants/{tenantAId}/staff/{userAId}/sessions/revoke", null);

        var refreshOther = await clientOther.PostAsync("/api/tenant/auth/refresh", null);
        Assert.Equal(HttpStatusCode.OK, refreshOther.StatusCode);
    }
}
