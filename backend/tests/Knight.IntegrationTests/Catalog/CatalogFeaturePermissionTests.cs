using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Knight.Application.Abstractions.Features;
using Knight.Contracts.Catalog;
using Knight.Contracts.Common;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// The catalog surface is gated by two independent conditions: the tenant must
/// have the <c>catalog</c> feature enabled, and the caller must hold the relevant
/// <c>catalog.*</c> permission. These tests drive the same endpoint three ways to
/// prove neither condition alone is sufficient — see docs/architecture/catalog.md.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogFeaturePermissionTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogFeaturePermissionTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PermissionHeldButFeatureDisabled_IsDenied()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: false, "catalog.products.view");
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.GetAsync("/api/tenant/catalog/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task NeitherFeatureNorPermissionHeld_IsDenied()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: false);
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.GetAsync("/api/tenant/catalog/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// A feature key the platform has never registered has no
    /// <c>FeatureDefinition</c> row and therefore no <c>TenantFeature</c> row for
    /// any tenant. The gate must read that as denied rather than as
    /// "nothing forbids it" — distinct from the registered-but-disabled case,
    /// which is asserted alongside it here.
    /// </summary>
    [Fact]
    public async Task UnregisteredFeatureKey_FailsClosed()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: true, "catalog.products.view");
        var unknownKey = $"never-registered-{Guid.NewGuid():n}";

        var (definitionExists, unknownEnabled, knownButDisabledEnabled) = await _fixture.WithScopeAsync(
            async (context, sp) =>
            {
                var featureAccess = sp.GetRequiredService<IFeatureAccessService>();

                var exists = await context.FeatureDefinitions.AnyAsync(d => d.Key == unknownKey);
                var unknown = await featureAccess.IsEnabledAsync(tenant.TenantId, unknownKey, CancellationToken.None);

                // A second tenant with the definition present but the flag off,
                // to show the two situations are told apart by cause, not outcome.
                var disabledTenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: false);
                var known = await featureAccess.IsEnabledAsync(
                    disabledTenant.TenantId, global::Catalog.CatalogFeature.Key, CancellationToken.None);

                return (exists, unknown, known);
            },
            platformContext: true);

        Assert.False(definitionExists);
        Assert.False(unknownEnabled);
        Assert.False(knownButDisabledEnabled);
    }

    /// <summary>
    /// The gate runs before the endpoint handler, so a denied write never reaches
    /// the application service — proven by the absence of any row, not merely by
    /// the status code.
    /// </summary>
    [Fact]
    public async Task FeatureDisabled_CreateProduct_IsDeniedBeforeAnyRowIsWritten()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: false, PostgresApiFixture.AllCatalogPermissions());
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.PostAsJsonAsync("/api/tenant/catalog/products", new CreateProductRequest
        {
            CategoryId = categoryId,
            Name = "Should Never Exist",
            BasePrice = 12m
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var productCount = await _fixture.WithScopeAsync(
            (context, _) => context.Products.CountAsync(p => p.TenantId == tenant.TenantId),
            platformContext: true);

        Assert.Equal(0, productCount);
    }

    [Fact]
    public async Task FeatureEnabledButPermissionMissing_IsDenied()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: true);
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.GetAsync("/api/tenant/catalog/products");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task FeatureEnabledAndPermissionHeld_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: true, "catalog.products.view");
        var client = CatalogTestClient.For(_fixture, tenant);

        var response = await client.GetAsync("/api/tenant/catalog/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task FeatureExplicitlyDisabledAfterBeingEnabled_RevokesAccess()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: true, "catalog.products.view");
        var client = CatalogTestClient.For(_fixture, tenant);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/tenant/catalog/products")).StatusCode);

        await _fixture.SetCatalogFeatureAsync(tenant.TenantId, isEnabled: false);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/tenant/catalog/products")).StatusCode);
    }

    [Fact]
    public async Task AnonymousCaller_CannotReachTheTenantAdminCatalog()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: true, "catalog.products.view");
        var client = CatalogTestClient.For(_fixture, tenant.Host);

        var response = await client.GetAsync("/api/tenant/catalog/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The storefront routes carry the feature gate but no authorization policy.
    /// An anonymous request with no bearer token must therefore be judged by the
    /// feature check — 403 because the feature is off, never 401 for a missing
    /// identity the route never asked for.
    /// </summary>
    [Fact]
    public async Task PublicCatalog_WithFeatureDisabled_IsDenied()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: false);
        var client = CatalogTestClient.For(_fixture, tenant.Host);

        var categories = await client.GetAsync("/api/catalog/categories");
        var products = await client.GetAsync("/api/catalog/products");

        Assert.Equal(HttpStatusCode.Forbidden, categories.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, products.StatusCode);
    }

    [Fact]
    public async Task PublicCatalog_WithFeatureEnabled_ReturnsData()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(featureEnabled: true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");

        var client = CatalogTestClient.For(_fixture, tenant.Host);

        var categories = await client.GetFromJsonAsync<PagedResponse<PublicCategoryResponse>>("/api/catalog/categories");
        var products = await client.GetFromJsonAsync<PagedResponse<PublicProductSummaryResponse>>("/api/catalog/products");

        Assert.NotNull(categories);
        Assert.NotNull(products);
        Assert.Contains(categories!.Items, c => c.Slug == "category-a");
        Assert.Contains(products!.Items, p => p.Slug == "product-a");
    }
}
