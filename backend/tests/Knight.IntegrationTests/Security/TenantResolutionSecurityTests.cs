using System.Net;
using System.Net.Http.Headers;
using Knight.IntegrationTests.Infrastructure;
using Tenancy.Domain;
using Xunit;

namespace Knight.IntegrationTests.Security;

/// <summary>
/// Proves tenant resolution fails closed and rejects ambiguous signals, using the
/// real HTTP pipeline (TenantResolutionMiddleware -> DomainTenantResolver) against
/// a real PostgreSQL-backed tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantResolutionSecurityTests
{
    private readonly PostgresApiFixture _fixture;

    public TenantResolutionSecurityTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Me_WhenTokenTenantAndHostTenantDisagree_ReturnsForbidden()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var tenantAId = await SeedTenantAsync(activate: true);
        var tenantBHost = await SeedActiveTenantWithDomainAsync();

        var token = _fixture.CreateTenantUserToken(tenantAId);
        var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tenant/me");
        request.Headers.Host = tenantBHost;

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Me_WhenTenantIsSuspended_ReturnsForbidden()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var tenantId = await SeedTenantAsync(activate: true, suspend: true);
        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreateTenantUserToken(tenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/tenant/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Me_WhenTenantIsArchived_ReturnsForbidden()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var tenantId = await SeedTenantAsync(activate: true, archive: true);
        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreateTenantUserToken(tenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/tenant/me");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Me_WhenTenantIsActive_ReturnsOk()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var tenantId = await SeedTenantAsync(activate: true);
        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreateTenantUserToken(tenantId);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/tenant/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Me_WhenClaimedTenantNoLongerExists_ReturnsForbidden_NotDefaultTenant()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreateTenantUserToken(Guid.NewGuid()); // never persisted
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/tenant/me");

        // Absence of a resolvable tenant must never fall back to "all tenants" or
        // any default tenant — the request is rejected, not silently widened.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Me_AsPlatformAdminToken_IsNeverTreatedAsTenantUser()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreatePlatformAdminToken();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/tenant/me");

        // The "TenantUserOnly" policy must reject a platform-admin token outright.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private async Task<Guid> SeedTenantAsync(bool activate = false, bool suspend = false, bool archive = false)
    {
        var id = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var tenant = Tenant.Create(id, now, $"Tenant {suffix}", $"tenant-{suffix}", "UTC", "USD");
            if (activate)
            {
                tenant.Activate(now);
            }

            if (suspend)
            {
                tenant.Suspend(now);
            }

            if (archive)
            {
                tenant.Archive(now);
            }

            await context.Tenants.AddAsync(tenant);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return id;
    }

    private async Task<string> SeedActiveTenantWithDomainAsync()
    {
        var suffix = Guid.NewGuid().ToString("n")[..8];
        var now = DateTimeOffset.UtcNow;
        var host = string.Empty;

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var tenant = Tenant.Create(Guid.NewGuid(), now, $"Tenant {suffix}", $"tenant-{suffix}", "UTC", "USD");
            tenant.Activate(now);
            var domain = tenant.AddDomain(Guid.NewGuid(), $"tenant-{suffix}.example.test", TenantDomainType.Primary, makePrimary: true, now);
            host = domain.Host;

            await context.Tenants.AddAsync(tenant);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return host;
    }
}
