using System.Net;
using System.Net.Http.Json;
using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Catalog;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Catalog;

/// <summary>
/// Catalog tenant isolation at both levels: the tenant-scoped API must answer a
/// cross-tenant identifier with a plain not-found that reveals nothing, and the
/// database's composite foreign keys must reject a cross-tenant row even when the
/// application layer is bypassed entirely — the same pattern proven for Identity
/// in <see cref="AccessControl.RoleTenantIsolationTests"/>.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CatalogTenantIsolationTests
{
    private readonly PostgresApiFixture _fixture;

    public CatalogTenantIsolationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(CatalogTenantContext Alpha, CatalogTenantContext Beta)> SeedTwoTenantsAsync()
    {
        var alpha = await _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());
        var beta = await _fixture.SeedCatalogTenantAsync(true, PostgresApiFixture.AllCatalogPermissions());
        return (alpha, beta);
    }

    [Fact]
    public async Task TenantAlpha_CannotReadTenantBetaCategory()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");

        var client = CatalogTestClient.For(_fixture, alpha);
        var response = await client.GetAsync($"/api/tenant/catalog/categories/{betaCategoryId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNoTenantLeakage(body, beta);
    }

    [Fact]
    public async Task TenantAlpha_CannotUpdateTenantBetaCategory()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");

        var client = CatalogTestClient.For(_fixture, alpha);
        var response = await client.PutAsJsonAsync(
            $"/api/tenant/catalog/categories/{betaCategoryId}",
            new UpdateCategoryRequest { Name = "Category A" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNoTenantLeakage(body, beta);

        var unchanged = await _fixture.WithScopeAsync(
            (context, _) => context.Categories.AsNoTracking().FirstAsync(c => c.Id == betaCategoryId),
            platformContext: true);

        Assert.Equal("Category B", unchanged.Name);
    }

    [Fact]
    public async Task TenantAlpha_CannotDeleteTenantBetaCategory()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");

        var client = CatalogTestClient.For(_fixture, alpha);
        var response = await client.DeleteAsync($"/api/tenant/catalog/categories/{betaCategoryId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNoTenantLeakage(body, beta);

        var stillExists = await _fixture.WithScopeAsync(
            (context, _) => context.Categories.AnyAsync(c => c.Id == betaCategoryId),
            platformContext: true);

        Assert.True(stillExists);
    }

    [Fact]
    public async Task TenantAlpha_CannotReadTenantBetaProduct()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");
        var betaProductId = await _fixture.SeedProductAsync(beta.TenantId, betaCategoryId, "Product B");

        var client = CatalogTestClient.For(_fixture, alpha);
        var response = await client.GetAsync($"/api/tenant/catalog/products/{betaProductId}");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNoTenantLeakage(body, beta);
    }

    [Fact]
    public async Task TenantAlpha_CannotUpdateOrArchiveTenantBetaProduct()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");
        var betaProductId = await _fixture.SeedProductAsync(beta.TenantId, betaCategoryId, "Product B");

        var client = CatalogTestClient.For(_fixture, alpha);

        var update = await client.PutAsJsonAsync(
            $"/api/tenant/catalog/products/{betaProductId}",
            new UpdateProductRequest { Name = "Product A", BasePrice = 5m, IsVisible = true, IsAvailable = true, DisplayOrder = 0 });
        var archive = await client.DeleteAsync($"/api/tenant/catalog/products/{betaProductId}");

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, archive.StatusCode);
        AssertNoTenantLeakage(await update.Content.ReadAsStringAsync(), beta);
        AssertNoTenantLeakage(await archive.Content.ReadAsStringAsync(), beta);

        var product = await _fixture.WithScopeAsync(
            (context, _) => context.Products.AsNoTracking().FirstAsync(p => p.Id == betaProductId),
            platformContext: true);

        Assert.Equal("Product B", product.Name);
        Assert.Equal(ProductStatus.Active, product.Status);
    }

    [Fact]
    public async Task TenantAlphaList_NeverIncludesTenantBetaProducts()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");
        var betaProductId = await _fixture.SeedProductAsync(beta.TenantId, betaCategoryId, "Product B");

        var client = CatalogTestClient.For(_fixture, alpha);
        var body = await client.GetStringAsync("/api/tenant/catalog/products");

        Assert.DoesNotContain(betaProductId.ToString(), body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CrossTenantProductToCategory_IsRejectedByDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");

        // Composite FK (TenantId, CategoryId) -> categories(TenantId, Id): the
        // category only exists under Tenant Beta, so PostgreSQL rejects the row.
        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var invalid = Product.Create(
                Guid.NewGuid(), DateTimeOffset.UtcNow, alpha.TenantId, betaCategoryId,
                "Product A", null, null, ProductStatus.Draft, 10m, true, true, 0);
            await context.Products.AddAsync(invalid);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task CrossTenantVariantToProduct_IsRejectedByDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");
        var betaProductId = await _fixture.SeedProductAsync(beta.TenantId, betaCategoryId, "Product B");

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var invalid = ProductVariant.Create(
                Guid.NewGuid(), DateTimeOffset.UtcNow, alpha.TenantId, betaProductId,
                "Variant A", null, 10m, null, false, true, 0);
            await context.ProductVariants.AddAsync(invalid);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task CrossTenantModifierToModifierGroup_IsRejectedByDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaGroupId = await _fixture.SeedModifierGroupAsync(beta.TenantId, "Group B");

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var invalid = Modifier.Create(
                Guid.NewGuid(), DateTimeOffset.UtcNow, alpha.TenantId, betaGroupId, "Modifier A", 1m, true, 0);
            await context.Modifiers.AddAsync(invalid);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task CrossTenantAssignmentToProduct_IsRejectedByDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");
        var betaProductId = await _fixture.SeedProductAsync(beta.TenantId, betaCategoryId, "Product B");
        var alphaGroupId = await _fixture.SeedModifierGroupAsync(alpha.TenantId, "Group A");

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var invalid = ProductModifierGroup.Create(
                Guid.NewGuid(), DateTimeOffset.UtcNow, alpha.TenantId, betaProductId, alphaGroupId, 0);
            await context.ProductModifierGroups.AddAsync(invalid);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task CrossTenantAssignmentToModifierGroup_IsRejectedByDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var alphaCategoryId = await _fixture.SeedCategoryAsync(alpha.TenantId, "Category A");
        var alphaProductId = await _fixture.SeedProductAsync(alpha.TenantId, alphaCategoryId, "Product A");
        var betaGroupId = await _fixture.SeedModifierGroupAsync(beta.TenantId, "Group B");

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var invalid = ProductModifierGroup.Create(
                Guid.NewGuid(), DateTimeOffset.UtcNow, alpha.TenantId, alphaProductId, betaGroupId, 0);
            await context.ProductModifierGroups.AddAsync(invalid);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    [Fact]
    public async Task CrossTenantMediaToProduct_IsRejectedByDatabase()
    {
        if (!_fixture.IsAvailable) return;

        var (alpha, beta) = await SeedTwoTenantsAsync();
        var betaCategoryId = await _fixture.SeedCategoryAsync(beta.TenantId, "Category B");
        var betaProductId = await _fixture.SeedProductAsync(beta.TenantId, betaCategoryId, "Product B");

        await Assert.ThrowsAsync<DbUpdateException>(() => _fixture.WithScopeAsync(async (context, _) =>
        {
            var invalid = ProductMedia.Create(
                Guid.NewGuid(), DateTimeOffset.UtcNow, alpha.TenantId, betaProductId,
                "products/abc123/photo.jpg", null, 0, false);
            await context.ProductMedia.AddAsync(invalid);
            await context.SaveChangesAsync();
        }, platformContext: true));
    }

    /// <summary>
    /// A scoped not-found must be indistinguishable from "never existed": the
    /// response may not carry the other tenant's identifier, host or any phrase
    /// implying the resource belongs to somebody else.
    /// </summary>
    private static void AssertNoTenantLeakage(string body, CatalogTenantContext other)
    {
        Assert.DoesNotContain(other.TenantId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(other.Host, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("another tenant", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("belongs to", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("other tenant", body, StringComparison.OrdinalIgnoreCase);
    }
}
