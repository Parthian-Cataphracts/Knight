using System.Net;
using System.Net.Http.Json;
using Catalog.Domain;
using Knight.Contracts.Catalog;
using Knight.Contracts.Common;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// The storefront visibility matrix. Only an Active, visible product is public.
/// "Unavailable" is not the same as "hidden": an unavailable product is still
/// listed, carrying <c>IsAvailable = false</c>, so a storefront can render it as
/// sold out rather than pretending it does not exist.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogPublicVisibilityTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogPublicVisibilityTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Matrix(
        CatalogTenantContext Tenant,
        Guid CategoryId,
        Guid VisibleAvailableId,
        Guid VisibleUnavailableId,
        Guid HiddenId,
        Guid DraftId,
        Guid ArchivedId);

    private async Task<Matrix> SeedMatrixAsync()
    {
        var tenant = await _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");

        var visibleAvailable = await _fixture.SeedProductAsync(
            tenant.TenantId, categoryId, "Product A", "product-a", ProductStatus.Active, 10m, isVisible: true, isAvailable: true);
        var visibleUnavailable = await _fixture.SeedProductAsync(
            tenant.TenantId, categoryId, "Product B", "product-b", ProductStatus.Active, 11m, isVisible: true, isAvailable: false);
        var hidden = await _fixture.SeedProductAsync(
            tenant.TenantId, categoryId, "Product C", "product-c", ProductStatus.Active, 12m, isVisible: false, isAvailable: true);
        var draft = await _fixture.SeedProductAsync(
            tenant.TenantId, categoryId, "Product D", "product-d", ProductStatus.Draft, 13m, isVisible: true, isAvailable: true);
        var archived = await _fixture.SeedProductAsync(
            tenant.TenantId, categoryId, "Product E", "product-e", ProductStatus.Archived, 14m, isVisible: true, isAvailable: true);

        return new Matrix(tenant, categoryId, visibleAvailable, visibleUnavailable, hidden, draft, archived);
    }

    [Fact]
    public async Task PublicProductList_ReturnsOnlyActiveVisibleProducts()
    {
        if (!_fixture.IsAvailable) return;

        var matrix = await SeedMatrixAsync();
        var client = CatalogTestClient.For(_fixture, matrix.Tenant.Host);

        var page = await client.GetFromJsonAsync<PagedResponse<PublicProductSummaryResponse>>("/api/catalog/products");

        Assert.NotNull(page);
        var ids = page!.Items.Select(p => p.Id).ToArray();

        Assert.Contains(matrix.VisibleAvailableId, ids);
        Assert.Contains(matrix.VisibleUnavailableId, ids);
        Assert.DoesNotContain(matrix.HiddenId, ids);
        Assert.DoesNotContain(matrix.DraftId, ids);
        Assert.DoesNotContain(matrix.ArchivedId, ids);
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task PublicProductList_ReturnsUnavailableProductsFlaggedRatherThanHidden()
    {
        if (!_fixture.IsAvailable) return;

        var matrix = await SeedMatrixAsync();
        var client = CatalogTestClient.For(_fixture, matrix.Tenant.Host);

        var page = await client.GetFromJsonAsync<PagedResponse<PublicProductSummaryResponse>>("/api/catalog/products");
        var unavailable = page!.Items.Single(p => p.Id == matrix.VisibleUnavailableId);
        var available = page.Items.Single(p => p.Id == matrix.VisibleAvailableId);

        Assert.False(unavailable.IsAvailable);
        Assert.True(available.IsAvailable);
    }

    [Fact]
    public async Task PublicProductDetail_ResolvesVisibleActiveSlugsOnly()
    {
        if (!_fixture.IsAvailable) return;

        var matrix = await SeedMatrixAsync();
        var client = CatalogTestClient.For(_fixture, matrix.Tenant.Host);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/products/product-a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/products/product-b")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/catalog/products/product-c")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/catalog/products/product-d")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/catalog/products/product-e")).StatusCode);
    }

    [Fact]
    public async Task PublicProductDetail_CarriesTheTenantDefaultCurrencyAndBasePrice()
    {
        if (!_fixture.IsAvailable) return;

        var matrix = await SeedMatrixAsync();
        var client = CatalogTestClient.For(_fixture, matrix.Tenant.Host);

        var detail = await client.GetFromJsonAsync<PublicProductDetailResponse>("/api/catalog/products/product-a");

        Assert.NotNull(detail);
        Assert.Equal("USD", detail!.Currency);
        // With no variants the base price is the authoritative, buyable price.
        Assert.False(detail.HasVariants);
        Assert.Equal(10m, detail.BasePrice);
    }

    [Fact]
    public async Task PublicProductDetail_SuppressesBasePriceOnceVariantsExist()
    {
        if (!_fixture.IsAvailable) return;

        var matrix = await SeedMatrixAsync();
        var adminClient = CatalogTestClient.For(_fixture, matrix.Tenant);

        await adminClient.PostAsJsonAsync($"/api/tenant/catalog/products/{matrix.VisibleAvailableId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Price = 21.5m });

        var client = CatalogTestClient.For(_fixture, matrix.Tenant.Host);
        var detail = await client.GetFromJsonAsync<PublicProductDetailResponse>("/api/catalog/products/product-a");

        Assert.NotNull(detail);
        Assert.True(detail!.HasVariants);
        Assert.Null(detail.BasePrice);
        Assert.Equal(21.5m, Assert.Single(detail.Variants).Price);
    }

    [Fact]
    public async Task PublicCategoryList_ExcludesHiddenCategories()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());
        await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A", "category-a", isVisible: true);
        await _fixture.SeedCategoryAsync(tenant.TenantId, "Category B", "category-b", isVisible: false);

        var client = CatalogTestClient.For(_fixture, tenant.Host);

        var page = await client.GetFromJsonAsync<PagedResponse<PublicCategoryResponse>>("/api/catalog/categories");

        Assert.NotNull(page);
        Assert.Contains(page!.Items, c => c.Slug == "category-a");
        Assert.DoesNotContain(page.Items, c => c.Slug == "category-b");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/catalog/categories/category-a")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/catalog/categories/category-b")).StatusCode);
    }

    [Fact]
    public async Task PublicProductList_FiltersByCategory()
    {
        if (!_fixture.IsAvailable) return;

        var matrix = await SeedMatrixAsync();
        var otherCategoryId = await _fixture.SeedCategoryAsync(matrix.Tenant.TenantId, "Category B");
        await _fixture.SeedProductAsync(matrix.Tenant.TenantId, otherCategoryId, "Product F", "product-f");

        var client = CatalogTestClient.For(_fixture, matrix.Tenant.Host);

        var page = await client.GetFromJsonAsync<PagedResponse<PublicProductSummaryResponse>>(
            $"/api/catalog/products?categoryId={matrix.CategoryId}");

        Assert.NotNull(page);
        Assert.All(page!.Items, p => Assert.Equal(matrix.CategoryId, p.CategoryId));
        Assert.Equal(2, page.Items.Count);
    }

    [Fact]
    public async Task PublicCatalog_NeverReturnsAnotherTenantsProducts()
    {
        if (!_fixture.IsAvailable) return;

        var alpha = await SeedMatrixAsync();
        var beta = await SeedMatrixAsync();

        var client = CatalogTestClient.For(_fixture, alpha.Tenant.Host);
        var page = await client.GetFromJsonAsync<PagedResponse<PublicProductSummaryResponse>>("/api/catalog/products");

        Assert.NotNull(page);
        Assert.DoesNotContain(beta.VisibleAvailableId, page!.Items.Select(p => p.Id));
    }
}
