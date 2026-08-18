using System.Net;
using System.Net.Http.Json;
using Knight.Contracts.Catalog;
using Knight.Contracts.Common;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// The platform mirror of the tenant catalog surface. A PlatformAdmin needs no
/// <c>catalog.*</c> permission — its authority is the policy itself — but the
/// target tenant's catalog feature is still checked, so a platform admin cannot
/// administer a capability the tenant does not have.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PlatformCatalogEndpointTests
{
    private readonly PostgresApiFixture _fixture;

    public PlatformCatalogEndpointTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlatformAdmin_CanManageATenantCatalogWithoutCatalogPermissions()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: true);
        var client = CatalogTestClient.For(_fixture, tenant.Host, _fixture.CreatePlatformAdminToken());

        var created = await client.PostAsJsonAsync(
            $"/api/platform/tenants/{tenant.TenantId}/catalog/categories",
            new CreateCategoryRequest { Name = "Category A" });
        var category = await created.Content.ReadFromJsonAsync<CategoryResponse>();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.NotNull(category);

        var listed = await client.GetFromJsonAsync<PagedResponse<CategoryResponse>>(
            $"/api/platform/tenants/{tenant.TenantId}/catalog/categories");

        Assert.NotNull(listed);
        Assert.Contains(listed!.Items, c => c.Id == category!.Id);

        var fetched = await client.GetAsync(
            $"/api/platform/tenants/{tenant.TenantId}/catalog/categories/{category!.Id}");

        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_IsStillBlockedWhenTheTargetTenantLacksTheCatalogFeature()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: false);
        var client = CatalogTestClient.For(_fixture, tenant.Host, _fixture.CreatePlatformAdminToken());

        var response = await client.GetAsync($"/api/platform/tenants/{tenant.TenantId}/catalog/categories");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task TenantUser_CannotReachThePlatformCatalogRoutes()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.GetAsync($"/api/platform/tenants/{tenant.TenantId}/catalog/categories");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
