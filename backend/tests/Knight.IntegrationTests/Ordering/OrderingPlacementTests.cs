using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering;
using Ordering.Domain;
using Knight.Application.Exceptions;
using Knight.Contracts.Common;
using Knight.Contracts.Ordering;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Ordering;

[Collection(PostgresCollection.Name)]
public sealed class OrderingPlacementTests
{
    private readonly PostgresApiFixture _fixture;

    public OrderingPlacementTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PlaceOrder_ValidCatalogSelections_SucceedsWithServerCalculatedPrices()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true,
            permissions: PostgresApiFixture.AllOrderingPermissions());

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Espresso", basePrice: 3.00m);

        var group = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, "Syrup", isRequired: false, minSelections: 0, maxSelections: 2, displayOrder: 0);
        var mod1 = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, group.Id, "Caramel", priceDelta: 0.75m, isAvailable: true, displayOrder: 0);

        var assignment = global::Catalog.Domain.ProductModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, productId, group.Id, 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ModifierGroups.AddAsync(group);
            await context.Modifiers.AddAsync(mod1);
            await context.ProductModifierGroups.AddAsync(assignment);
            await context.SaveChangesAsync();
        }, platformContext: true);

        PlaceOrderResult result = null!;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();
            result = await placementService.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                [
                    new PlaceOrderItemInput(productId, VariantId: null, Quantity: 2, ModifierIds: [mod1.Id])
                ]),
                new OrderActorContext(tenant.UserId, Knight.Application.Abstractions.Identity.PrincipalType.TenantUser),
                CancellationToken.None);
        }, platformContext: true);

        Assert.NotEqual(Guid.Empty, result.OrderId);
        Assert.True(result.OrderNumber >= 1001);
        Assert.Equal(OrderStatus.Pending, result.Status);
        Assert.Equal("USD", result.Currency);
        Assert.Equal(7.50m, result.Subtotal); // (3.00 + 0.75) * 2 = 7.50
        Assert.Equal(7.50m, result.Total);

        // Verify database persistence
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var savedOrder = await context.Orders
                .Include(o => o.Items)
                    .ThenInclude(i => i.Modifiers)
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.Id == result.OrderId);

            Assert.Equal(tenant.TenantId, savedOrder.TenantId);
            Assert.Single(savedOrder.Items);

            var savedItem = savedOrder.Items.First();
            Assert.Equal("Espresso", savedItem.ProductName);
            Assert.Equal(3.00m, savedItem.UnitBasePrice);
            Assert.Equal(0.75m, savedItem.UnitModifierTotal);
            Assert.Equal(3.75m, savedItem.UnitPrice);
            Assert.Equal(7.50m, savedItem.LineTotal);
            Assert.Equal(2, savedItem.Quantity);

            Assert.Single(savedItem.Modifiers);
            var savedMod = savedItem.Modifiers.First();
            Assert.Equal("Syrup", savedMod.ModifierGroupName);
            Assert.Equal("Caramel", savedMod.ModifierName);
            Assert.Equal(0.75m, savedMod.UnitPriceDelta);

            Assert.Single(savedOrder.StatusHistory);
            var history = savedOrder.StatusHistory.First();
            Assert.Null(history.FromStatus);
            Assert.Equal(OrderStatus.Pending, history.ToStatus);
            Assert.Equal(tenant.UserId, history.ChangedByUserId);
        }, platformContext: true);
    }

    [Fact]
    public async Task PlaceOrder_CrossTenantProduct_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var tenantB = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);

        var catB = await _fixture.SeedCategoryAsync(tenantB.TenantId, "Tenant B Category");
        var prodB = await _fixture.SeedProductAsync(tenantB.TenantId, catB, "Tenant B Secret Product");

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenantA.TenantId,
                    new PlaceOrderInput([new PlaceOrderItemInput(prodB, null, 1)]),
                    null,
                    CancellationToken.None));

            Assert.Contains("productId", ex.Errors.Keys);
        }, platformContext: true);
    }

    [Fact]
    public async Task PlaceOrder_ProductVariantMismatch_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var cat = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var prod1 = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Product 1");
        var prod2 = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Product 2");

        var variant2 = global::Catalog.Domain.ProductVariant.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, prod2, "Prod2 Variant", sku: null, price: 15.00m, compareAtPrice: null, isDefault: true, isAvailable: true, displayOrder: 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ProductVariants.AddAsync(variant2);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            // Attempt: select Prod 1 with Variant from Prod 2
            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput([new PlaceOrderItemInput(prod1, variant2.Id, 1)]),
                    null,
                    CancellationToken.None));

            Assert.Contains("variantId", ex.Errors.Keys);
        }, platformContext: true);
    }

    [Fact]
    public async Task PlaceOrder_ProductModifierMismatch_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var cat = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var prod = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Burger");

        var unassignedGroup = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, "Sauces", isRequired: false, minSelections: 0, maxSelections: 3, displayOrder: 0);
        var unassignedMod = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, unassignedGroup.Id, "Mayo", priceDelta: 0.50m, isAvailable: true, displayOrder: 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ModifierGroups.AddAsync(unassignedGroup);
            await context.Modifiers.AddAsync(unassignedMod);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput([new PlaceOrderItemInput(prod, null, 1, [unassignedMod.Id])]),
                    null,
                    CancellationToken.None));

            Assert.Contains("modifierIds", ex.Errors.Keys);
        }, platformContext: true);
    }

    [Fact]
    public async Task PlaceOrder_RequiredModifierGroupMissing_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var cat = await _fixture.SeedCategoryAsync(tenant.TenantId, "Drinks");
        var prod = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Tea");

        var reqGroup = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, "Sugar Level", isRequired: true, minSelections: 1, maxSelections: 1, displayOrder: 0);
        var mod = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, reqGroup.Id, "No Sugar", priceDelta: 0m, isAvailable: true, displayOrder: 0);
        var assign = global::Catalog.Domain.ProductModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, prod, reqGroup.Id, 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ModifierGroups.AddAsync(reqGroup);
            await context.Modifiers.AddAsync(mod);
            await context.ProductModifierGroups.AddAsync(assign);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            // Attempt: order Tea without selecting required Sugar Level
            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput([new PlaceOrderItemInput(prod, null, 1, ModifierIds: [])]),
                    null,
                    CancellationToken.None));

            Assert.Contains("modifiers", ex.Errors.Keys);
        }, platformContext: true);
    }

    [Fact]
    public async Task PlaceOrder_ModifierMaxSelectionsExceeded_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var cat = await _fixture.SeedCategoryAsync(tenant.TenantId, "Desserts");
        var prod = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Ice Cream");

        var group = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, "Toppings", isRequired: false, minSelections: 0, maxSelections: 2, displayOrder: 0);
        var mod1 = global::Catalog.Domain.Modifier.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, group.Id, "Nuts", 0.5m, true, 0);
        var mod2 = global::Catalog.Domain.Modifier.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, group.Id, "Sprinkles", 0.5m, true, 1);
        var mod3 = global::Catalog.Domain.Modifier.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, group.Id, "Cherry", 0.5m, true, 2);
        var assign = global::Catalog.Domain.ProductModifierGroup.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, prod, group.Id, 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ModifierGroups.AddAsync(group);
            await context.Modifiers.AddRangeAsync(mod1, mod2, mod3);
            await context.ProductModifierGroups.AddAsync(assign);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            // Attempt: 3 toppings selected when max is 2
            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput([new PlaceOrderItemInput(prod, null, 1, ModifierIds: [mod1.Id, mod2.Id, mod3.Id])]),
                    null,
                    CancellationToken.None));

            Assert.Contains("modifiers", ex.Errors.Keys);
        }, platformContext: true);
    }

    [Theory]
    [InlineData("Unavailable", false, true, global::Catalog.Domain.ProductStatus.Active)]
    [InlineData("Hidden", true, false, global::Catalog.Domain.ProductStatus.Active)]
    [InlineData("Draft", true, true, global::Catalog.Domain.ProductStatus.Draft)]
    [InlineData("Archived", true, true, global::Catalog.Domain.ProductStatus.Archived)]
    public async Task PlaceOrder_UnorderableProductStates_Rejected(
        string testName,
        bool isAvailable,
        bool isVisible,
        global::Catalog.Domain.ProductStatus status)
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var cat = await _fixture.SeedCategoryAsync(tenant.TenantId, $"Category {testName}");
        var prod = await _fixture.SeedProductAsync(
            tenant.TenantId, cat, $"Product {testName}", status: status, isVisible: isVisible, isAvailable: isAvailable);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput([new PlaceOrderItemInput(prod, null, 1)]),
                    null,
                    CancellationToken.None));
        }, platformContext: true);
    }

    [Fact]
    public async Task PlaceOrder_UnavailableVariant_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var cat = await _fixture.SeedCategoryAsync(tenant.TenantId, "Drinks");
        var prod = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Smoothie");

        var variant = global::Catalog.Domain.ProductVariant.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, prod, "Large", sku: null, price: 6.00m, compareAtPrice: null, isDefault: true, isAvailable: false, displayOrder: 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ProductVariants.AddAsync(variant);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput([new PlaceOrderItemInput(prod, variant.Id, 1)]),
                    null,
                    CancellationToken.None));

            Assert.Contains("variantId", ex.Errors.Keys);
        }, platformContext: true);
    }

    [Fact]
    public async Task PlaceOrder_UnavailableModifier_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var cat = await _fixture.SeedCategoryAsync(tenant.TenantId, "Drinks");
        var prod = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Coffee");

        var group = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, "Dairy", isRequired: false, minSelections: 0, maxSelections: 1, displayOrder: 0);
        var mod = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, group.Id, "Almond Milk", priceDelta: 0.75m, isAvailable: false, displayOrder: 0);

        var assign = global::Catalog.Domain.ProductModifierGroup.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenant.TenantId, prod, group.Id, 0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ModifierGroups.AddAsync(group);
            await context.Modifiers.AddAsync(mod);
            await context.ProductModifierGroups.AddAsync(assign);
            await context.SaveChangesAsync();
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            var ex = await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput([new PlaceOrderItemInput(prod, null, 1, ModifierIds: [mod.Id])]),
                    null,
                    CancellationToken.None));

            Assert.Contains("modifierIds", ex.Errors.Keys);
        }, platformContext: true);
    }

    [Fact]
    public async Task PlaceOrder_MultiItemAtomicRollbackOnFailure_NoPartialPersistence()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true);
        var cat = await _fixture.SeedCategoryAsync(tenant.TenantId, "Bakery");

        var validProd1 = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Croissant", basePrice: 3.50m);
        var validProd2 = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Baguette", basePrice: 2.50m);
        var invalidProd3 = await _fixture.SeedProductAsync(tenant.TenantId, cat, "Unavailable Cake", basePrice: 25.00m, isAvailable: false);

        int initialOrderCount = 0;
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            initialOrderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();

            // Attempt: order 3 items, item 3 is invalid (unavailable)
            await Assert.ThrowsAsync<ValidationException>(() =>
                placementService.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput(
                    [
                        new PlaceOrderItemInput(validProd1, null, 2),
                        new PlaceOrderItemInput(validProd2, null, 1),
                        new PlaceOrderItemInput(invalidProd3, null, 1)
                    ]),
                    null,
                    CancellationToken.None));
        }, platformContext: true);

        // Verify: ZERO orders, ZERO items, ZERO modifiers persisted
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var currentOrderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            Assert.Equal(initialOrderCount, currentOrderCount);

            var itemsCount = await context.OrderItems.CountAsync(i => i.TenantId == tenant.TenantId);
            Assert.Equal(0, itemsCount);
        }, platformContext: true);
    }
}
