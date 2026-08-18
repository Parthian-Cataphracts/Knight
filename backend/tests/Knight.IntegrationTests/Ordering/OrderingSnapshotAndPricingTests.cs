using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering;
using Ordering.Domain;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Ordering;

[Collection(PostgresCollection.Name)]
public sealed class OrderingSnapshotAndPricingTests
{
    private readonly PostgresApiFixture _fixture;

    public OrderingSnapshotAndPricingTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task HistoricalSnapshot_SurvivesCatalogEditsAndRenames()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Classic Burger", basePrice: 10.00m);

        var variant = global::Catalog.Domain.ProductVariant.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, productId, "Single Patty", sku: null, price: 12.00m, compareAtPrice: null, isDefault: true, isAvailable: true, displayOrder: 0);

        var group = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, "Add-ons", isRequired: false, minSelections: 0, maxSelections: 3, displayOrder: 0);

        var mod = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, group.Id, "Extra Cheese", priceDelta: 1.50m, isAvailable: true, displayOrder: 0);

        var assign = global::Catalog.Domain.ProductModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, productId, group.Id, 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ProductVariants.AddAsync(variant);
            await context.ModifierGroups.AddAsync(group);
            await context.Modifiers.AddAsync(mod);
            await context.ProductModifierGroups.AddAsync(assign);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Place order
        PlaceOrderResult orderResult = null!;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();
            orderResult = await placementService.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                [
                    new PlaceOrderItemInput(productId, variant.Id, Quantity: 2, ModifierIds: [mod.Id])
                ]),
                null,
                CancellationToken.None);
        }, platformContext: true);

        // Expected initial: (12.00 + 1.50) * 2 = 27.00
        Assert.Equal(27.00m, orderResult.Total);

        // Now modify Catalog: Rename Product, change BasePrice, rename Variant, change Variant price, rename Modifier, change price delta
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var p = await context.Products.FirstAsync(p => p.Id == productId);
            p.UpdateDetails("Super Mega Burger", p.Slug, p.Description, 99.00m, p.IsVisible, p.IsAvailable, p.DisplayOrder, DateTimeOffset.UtcNow);

            var v = await context.ProductVariants.FirstAsync(v => v.Id == variant.Id);
            v.UpdateDetails("Double Patty Gourmet", "SKU-NEW", 88.00m, null, true, 0, DateTimeOffset.UtcNow);

            var m = await context.Modifiers.FirstAsync(m => m.Id == mod.Id);
            m.UpdateDetails("Truffle Cheese", 25.00m, true, 0, DateTimeOffset.UtcNow);

            var g = await context.ModifierGroups.FirstAsync(g => g.Id == group.Id);
            g.Rename("Luxury Extras", DateTimeOffset.UtcNow);

            await context.SaveChangesAsync();
        }, platformContext: true);

        // Read the historical order
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var managementService = sp.GetRequiredService<IOrderManagementService>();
            var historicalOrder = await managementService.GetByIdAsync(tenant.TenantId, orderResult.OrderId, CancellationToken.None);

            Assert.NotNull(historicalOrder);
            Assert.Equal(27.00m, historicalOrder.Subtotal);
            Assert.Equal(27.00m, historicalOrder.Total);

            var item = historicalOrder.Items.First();
            Assert.Equal("Classic Burger", item.ProductName);
            Assert.Equal("Single Patty", item.VariantName);
            Assert.Equal(12.00m, item.UnitBasePrice);
            Assert.Equal(1.50m, item.UnitModifierTotal);
            Assert.Equal(13.50m, item.UnitPrice);
            Assert.Equal(27.00m, item.LineTotal);
            Assert.Equal(2, item.Quantity);

            var itemMod = item.Modifiers.First();
            Assert.Equal("Add-ons", itemMod.ModifierGroupName);
            Assert.Equal("Extra Cheese", itemMod.ModifierName);
            Assert.Equal(1.50m, itemMod.UnitPriceDelta);
        }, platformContext: true);
    }

    [Fact]
    public async Task HistoricalSnapshot_SurvivesProductArchiving()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Desserts");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Seasonal Tart", basePrice: 8.50m);

        PlaceOrderResult orderResult = null!;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();
            orderResult = await placementService.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput([new PlaceOrderItemInput(productId, null, 1)]),
                null,
                CancellationToken.None);
        }, platformContext: true);

        // Archive product in Catalog
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var p = await context.Products.FirstAsync(p => p.Id == productId);
            p.Archive(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Historical order still loads with all details
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var managementService = sp.GetRequiredService<IOrderManagementService>();
            var historicalOrder = await managementService.GetByIdAsync(tenant.TenantId, orderResult.OrderId, CancellationToken.None);

            Assert.NotNull(historicalOrder);
            Assert.Equal(8.50m, historicalOrder.Total);
            Assert.Equal("Seasonal Tart", historicalOrder.Items.First().ProductName);
        }, platformContext: true);
    }

    [Fact]
    public async Task DecimalMonetaryPrecision_RoundTripsExactlyThroughPostgreSQL()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Gourmet");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Custom Steak", basePrice: 19.99m);

        var group = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, "Sides", false, 0, 2, 0);
        var mod = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, group.Id, "Truffle Butter", priceDelta: 3.49m, isAvailable: true, displayOrder: 0);
        var assign = global::Catalog.Domain.ProductModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, productId, group.Id, 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ModifierGroups.AddAsync(group);
            await context.Modifiers.AddAsync(mod);
            await context.ProductModifierGroups.AddAsync(assign);
            await context.SaveChangesAsync();
        }, platformContext: true);

        PlaceOrderResult orderResult = null!;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();
            orderResult = await placementService.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                [
                    new PlaceOrderItemInput(productId, null, Quantity: 3, ModifierIds: [mod.Id])
                ]),
                null,
                CancellationToken.None);
        }, platformContext: true);

        // (19.99 + 3.49) = 23.48 * 3 = 70.44
        Assert.Equal(70.44m, orderResult.Total);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var managementService = sp.GetRequiredService<IOrderManagementService>();
            var order = await managementService.GetByIdAsync(tenant.TenantId, orderResult.OrderId, CancellationToken.None);

            Assert.NotNull(order);
            Assert.Equal(19.99m, order.Items.First().UnitBasePrice);
            Assert.Equal(3.49m, order.Items.First().UnitModifierTotal);
            Assert.Equal(23.48m, order.Items.First().UnitPrice);
            Assert.Equal(70.44m, order.Items.First().LineTotal);
            Assert.Equal(70.44m, order.Subtotal);
            Assert.Equal(70.44m, order.Total);
        }, platformContext: true);
    }
}
