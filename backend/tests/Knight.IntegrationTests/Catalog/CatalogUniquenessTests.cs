using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Catalog;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// Slug and SKU uniqueness are declared per tenant on the normalized column, so
/// two tenants may legitimately use the same slug or SKU while one tenant may
/// not use it twice. Nullable SKUs must not collide with each other.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogUniquenessTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogUniquenessTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<CatalogTenantContext> SeedTenantAsync() =>
        _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());

    [Fact]
    public async Task DuplicateCategorySlug_WithinOneTenant_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var client = CatalogTestClient.For(_fixture, tenant);

        var first = await client.PostAsJsonAsync("/api/tenant/catalog/categories",
            new CreateCategoryRequest { Name = "Category A", Slug = "shared-slug" });
        // Different display name, same normalized slug — the normalized form is
        // what the unique index is declared on.
        var second = await client.PostAsJsonAsync("/api/tenant/catalog/categories",
            new CreateCategoryRequest { Name = "Category B", Slug = "  Shared   SLUG  " });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task SameCategorySlug_AcrossTwoTenants_BothSucceed()
    {
        if (!_fixture.IsAvailable) return;

        var alpha = await SeedTenantAsync();
        var beta = await SeedTenantAsync();

        var alphaResponse = await CatalogTestClient.For(_fixture, alpha).PostAsJsonAsync(
            "/api/tenant/catalog/categories", new CreateCategoryRequest { Name = "Category A", Slug = "shared-slug" });
        var betaResponse = await CatalogTestClient.For(_fixture, beta).PostAsJsonAsync(
            "/api/tenant/catalog/categories", new CreateCategoryRequest { Name = "Category A", Slug = "shared-slug" });

        Assert.Equal(HttpStatusCode.Created, alphaResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, betaResponse.StatusCode);
    }

    [Fact]
    public async Task DuplicateProductSlug_WithinOneTenant_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var first = await client.PostAsJsonAsync("/api/tenant/catalog/products",
            new CreateProductRequest { CategoryId = categoryId, Name = "Product A", Slug = "shared-product", BasePrice = 10m });
        var second = await client.PostAsJsonAsync("/api/tenant/catalog/products",
            new CreateProductRequest { CategoryId = categoryId, Name = "Product B", Slug = "Shared  Product", BasePrice = 12m });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task SameProductSlug_AcrossTwoTenants_BothSucceed()
    {
        if (!_fixture.IsAvailable) return;

        var alpha = await SeedTenantAsync();
        var beta = await SeedTenantAsync();
        var alphaCategoryId = await _fixture.SeedCategoryAsync(alpha.TenantId, "Category A");
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category A");

        var alphaResponse = await CatalogTestClient.For(_fixture, alpha).PostAsJsonAsync(
            "/api/tenant/catalog/products",
            new CreateProductRequest { CategoryId = alphaCategoryId, Name = "Product A", Slug = "shared-product", BasePrice = 10m });
        var betaResponse = await CatalogTestClient.For(_fixture, beta).PostAsJsonAsync(
            "/api/tenant/catalog/products",
            new CreateProductRequest { CategoryId = betaCategoryId, Name = "Product A", Slug = "shared-product", BasePrice = 10m });

        Assert.Equal(HttpStatusCode.Created, alphaResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, betaResponse.StatusCode);
    }

    [Fact]
    public async Task DuplicateVariantSku_WithinOneTenant_IsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");
        var client = CatalogTestClient.For(_fixture, tenant);

        var first = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Sku = "sku-a1", Price = 10m });
        // Same SKU in different casing: uniqueness is declared on the uppercase
        // normalized column, so casing cannot be used to slip a duplicate through.
        var second = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
            new CreateProductVariantRequest { Name = "Variant B", Sku = "SKU-A1", Price = 12m });

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task SameVariantSku_AcrossTwoTenants_BothSucceed()
    {
        if (!_fixture.IsAvailable) return;

        var alpha = await SeedTenantAsync();
        var beta = await SeedTenantAsync();
        var alphaProductId = await _fixture.SeedProductAsync(
            alpha.TenantId, await _fixture.SeedCategoryAsync(alpha.TenantId, "Category A"), "Product A");
        var betaProductId = await _fixture.SeedProductAsync(
            beta.TenantId, await _fixture.SeedCategoryAsync(beta.TenantId, "Category A"), "Product A");

        var alphaResponse = await CatalogTestClient.For(_fixture, alpha).PostAsJsonAsync(
            $"/api/tenant/catalog/products/{alphaProductId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Sku = "shared-sku", Price = 10m });
        var betaResponse = await CatalogTestClient.For(_fixture, beta).PostAsJsonAsync(
            $"/api/tenant/catalog/products/{betaProductId}/variants",
            new CreateProductVariantRequest { Name = "Variant A", Sku = "shared-sku", Price = 10m });

        Assert.Equal(HttpStatusCode.Created, alphaResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Created, betaResponse.StatusCode);
    }

    [Fact]
    public async Task MultipleVariantsWithNullSku_InOneTenant_AllSucceed()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await SeedTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category A");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product A");
        var client = CatalogTestClient.For(_fixture, tenant);

        // The uniqueness index is filtered on "NormalizedSku IS NOT NULL", so any
        // number of SKU-less variants may coexist in the same tenant.
        for (var index = 0; index < 3; index++)
        {
            var response = await client.PostAsJsonAsync($"/api/tenant/catalog/products/{productId}/variants",
                new CreateProductVariantRequest { Name = $"Variant {index}", Sku = null, Price = 10m });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var nullSkuCount = await _fixture.WithScopeAsync(
            (context, _) => context.ProductVariants.CountAsync(v => v.ProductId == productId && v.NormalizedSku == null),
            platformContext: true);

        Assert.Equal(3, nullSkuCount);
    }
}
