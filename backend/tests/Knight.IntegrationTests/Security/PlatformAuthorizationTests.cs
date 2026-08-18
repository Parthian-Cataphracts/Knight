using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Knight.Contracts.Platform;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Security;

/// <summary>
/// Proves the Platform tenant-management API is reachable only by an authenticated
/// Platform Super Admin — never by anonymous callers or tenant users, and never
/// merely because a request lacks tenant context.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlatformAuthorizationTests
{
    private readonly PostgresApiFixture _fixture;

    public PlatformAuthorizationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ListTenants_AsAnonymous_ReturnsUnauthorized()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.Factory.CreateClient();

        var response = await client.GetAsync("/api/platform/tenants");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ListTenants_AsTenantUser_ReturnsForbidden()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreateTenantUserToken(Guid.NewGuid());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/platform/tenants");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ListTenants_AsPlatformAdmin_ReturnsOk()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreatePlatformAdminToken();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/platform/tenants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task CreateTenant_AsPlatformAdmin_PersistsAndReturnsCreated()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreatePlatformAdminToken();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var suffix = Guid.NewGuid().ToString("n")[..8];
        var response = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest
        {
            Name = $"Generic Test Tenant {suffix}",
            Slug = $"generic-test-tenant-{suffix}",
            TimeZone = "UTC",
            DefaultCurrency = "USD"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<TenantResponse>();
        Assert.NotNull(body);
        Assert.Equal("Pending", body!.Status);
    }

    [Fact]
    public async Task CreateTenant_WithDuplicateSlug_ReturnsConflict()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreatePlatformAdminToken();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var suffix = Guid.NewGuid().ToString("n")[..8];
        var request = new CreateTenantRequest
        {
            Name = $"Duplicate Slug Tenant {suffix}",
            Slug = $"duplicate-slug-tenant-{suffix}",
            TimeZone = "UTC",
            DefaultCurrency = "USD"
        };

        var first = await client.PostAsJsonAsync("/api/platform/tenants", request);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync("/api/platform/tenants", request);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task AddDomain_WithHostAlreadyOwnedByAnotherTenant_ReturnsConflict()
    {
        if (!_fixture.IsAvailable)
        {
            return;
        }

        var client = _fixture.Factory.CreateClient();
        var token = _fixture.CreatePlatformAdminToken();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var suffix = Guid.NewGuid().ToString("n")[..8];
        var host = $"contested-{suffix}.example.test";

        var firstTenant = await CreateTenantAsync(client, suffix, "first");
        var secondTenant = await CreateTenantAsync(client, suffix, "second");

        var firstAdd = await client.PostAsJsonAsync($"/api/platform/tenants/{firstTenant!.Id}/domains", new AddTenantDomainRequest
        {
            Host = host,
            Type = "Primary",
            MakePrimary = true
        });
        Assert.Equal(HttpStatusCode.OK, firstAdd.StatusCode);

        var secondAdd = await client.PostAsJsonAsync($"/api/platform/tenants/{secondTenant!.Id}/domains", new AddTenantDomainRequest
        {
            Host = host,
            Type = "Primary",
            MakePrimary = true
        });

        Assert.Equal(HttpStatusCode.Conflict, secondAdd.StatusCode);
    }

    private static async Task<TenantResponse?> CreateTenantAsync(HttpClient client, string suffix, string label)
    {
        var response = await client.PostAsJsonAsync("/api/platform/tenants", new CreateTenantRequest
        {
            Name = $"{label} tenant {suffix}",
            Slug = $"{label}-tenant-{suffix}",
            TimeZone = "UTC",
            DefaultCurrency = "USD"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<TenantResponse>();
    }
}
