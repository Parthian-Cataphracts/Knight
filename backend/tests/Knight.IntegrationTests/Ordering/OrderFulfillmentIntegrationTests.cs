using System.Net;
using System.Net.Http.Json;
using Delivery;
using Delivery.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering;
using Ordering.Domain;
using Knight.Application.Exceptions;
using Knight.Contracts.Ordering;
using Knight.IntegrationTests.Catalog;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Ordering;

[Collection(PostgresCollection.Name)]
public sealed class OrderFulfillmentIntegrationTests
{
    private readonly PostgresApiFixture _fixture;

    public OrderFulfillmentIntegrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task PickupOrderPlacement_CalculatesZeroFee_AndExposesSnapshotInApi()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Espresso", basePrice: 4.50m);

        PlaceOrderResult result = null!;
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            result = await placement.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                    [new PlaceOrderItemInput(productId, null, 2)],
                    Fulfillment: new PlaceOrderFulfillmentInput(OrderFulfillmentMethod.Pickup, null, null)),
                null,
                CancellationToken.None);
        }, platformContext: true);

        Assert.NotNull(result);
        Assert.Equal(9.00m, result.Subtotal);
        Assert.Equal(9.00m, result.Total);

        // Inspect via Tenant Order Detail API
        var client = CatalogTestClient.For(_fixture, tenant);
        var orderResponse = await client.GetFromJsonAsync<OrderDetailResponse>($"/api/tenant/orders/{result.OrderId}");

        Assert.NotNull(orderResponse);
        Assert.Equal(9.00m, orderResponse.Subtotal);
        Assert.Equal(0.00m, orderResponse.FulfillmentFee);
        Assert.Equal(9.00m, orderResponse.Total);
        Assert.NotNull(orderResponse.Fulfillment);
        Assert.Equal("Pickup", orderResponse.Fulfillment.Method);
        Assert.Equal(0.00m, orderResponse.Fulfillment.Fee);
        Assert.Null(orderResponse.Fulfillment.Delivery);

        // Inspect via Tenant Order List API
        var listRes = await client.GetFromJsonAsync<Knight.Contracts.Common.PagedResponse<OrderSummaryResponse>>("/api/tenant/orders?page=1&pageSize=10");
        Assert.NotNull(listRes);
        var summary = listRes.Items.FirstOrDefault(o => o.Id == result.OrderId);
        Assert.NotNull(summary);
        Assert.Equal("Pickup", summary.FulfillmentMethod);
    }

    [Fact]
    public async Task DeliveryOrderPlacement_CalculatesServerAuthoritativeFee_AndSnapshotsAddress()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);
        var zone = await _fixture.SeedDeliveryZoneAsync(tenant.TenantId, "Uptown", fee: 5.50m, minimumOrderSubtotal: 10.00m);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Meals");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Pasta", basePrice: 12.00m);

        PlaceOrderResult result = null!;
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            result = await placement.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                    [new PlaceOrderItemInput(productId, null, 2)],
                    Fulfillment: new PlaceOrderFulfillmentInput(
                        OrderFulfillmentMethod.Delivery,
                        zone.Id,
                        new PlaceOrderAddressInput(
                            "742 Evergreen Terrace",
                            "Suite 100",
                            "Springfield",
                            "97477",
                            44.0462,
                            -123.0220))),
                null,
                CancellationToken.None);
        }, platformContext: true);

        Assert.NotNull(result);
        Assert.Equal(24.00m, result.Subtotal);
        Assert.Equal(29.50m, result.Total); // 24.00 + 5.50

        // Inspect via Tenant Order Detail API
        var client = CatalogTestClient.For(_fixture, tenant);
        var orderResponse = await client.GetFromJsonAsync<OrderDetailResponse>($"/api/tenant/orders/{result.OrderId}");

        Assert.NotNull(orderResponse);
        Assert.Equal(24.00m, orderResponse.Subtotal);
        Assert.Equal(5.50m, orderResponse.FulfillmentFee);
        Assert.Equal(29.50m, orderResponse.Total);
        Assert.NotNull(orderResponse.Fulfillment);
        Assert.Equal("Delivery", orderResponse.Fulfillment.Method);
        Assert.Equal(5.50m, orderResponse.Fulfillment.Fee);
        Assert.NotNull(orderResponse.Fulfillment.Delivery);
        Assert.Equal("Uptown", orderResponse.Fulfillment.Delivery.ZoneName);
        Assert.Equal("742 Evergreen Terrace", orderResponse.Fulfillment.Delivery.AddressLine1);
        Assert.Equal("Suite 100", orderResponse.Fulfillment.Delivery.AddressLine2);
        Assert.Equal("Springfield", orderResponse.Fulfillment.Delivery.City);
        Assert.Equal("97477", orderResponse.Fulfillment.Delivery.PostalCode);
        Assert.Equal(44.0462, orderResponse.Fulfillment.Delivery.Latitude);
        Assert.Equal(-123.0220, orderResponse.Fulfillment.Delivery.Longitude);
    }

    [Fact]
    public async Task DeliveryOrderPlacement_WhenDeliveryFeatureDisabled_ThrowsValidationException()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: false);
        var zone = await _fixture.SeedDeliveryZoneAsync(tenant.TenantId, "Zone1", fee: 5.00m);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Coffee", basePrice: 15.00m);

        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            await Assert.ThrowsAsync<ValidationException>(() =>
                placement.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput(
                        [new PlaceOrderItemInput(productId, null, 1)],
                        Fulfillment: new PlaceOrderFulfillmentInput(
                            OrderFulfillmentMethod.Delivery,
                            zone.Id,
                            new PlaceOrderAddressInput("123 Main St", null, "City", null, null, null))),
                    null,
                    CancellationToken.None));
        }, platformContext: true);
    }

    [Fact]
    public async Task DeliveryOrderPlacement_WhenNotAcceptingDeliveryOrders_ThrowsValidationException()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);
        await _fixture.SeedDeliverySettingsAsync(tenant.TenantId, isAcceptingDeliveryOrders: false);
        var zone = await _fixture.SeedDeliveryZoneAsync(tenant.TenantId, "Zone1", fee: 5.00m);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Coffee", basePrice: 15.00m);

        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            await Assert.ThrowsAsync<ValidationException>(() =>
                placement.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput(
                        [new PlaceOrderItemInput(productId, null, 1)],
                        Fulfillment: new PlaceOrderFulfillmentInput(
                            OrderFulfillmentMethod.Delivery,
                            zone.Id,
                            new PlaceOrderAddressInput("123 Main St", null, "City", null, null, null))),
                    null,
                    CancellationToken.None));
        }, platformContext: true);
    }

    [Fact]
    public async Task DeliveryOrderPlacement_WhenSubtotalBelowMinimum_ThrowsValidationException()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);
        var zone = await _fixture.SeedDeliveryZoneAsync(tenant.TenantId, "Zone1", fee: 5.00m, minimumOrderSubtotal: 30.00m);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Coffee", basePrice: 10.00m);

        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            // Subtotal is 20.00, below zone minimum of 30.00
            await Assert.ThrowsAsync<ValidationException>(() =>
                placement.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput(
                        [new PlaceOrderItemInput(productId, null, 2)],
                        Fulfillment: new PlaceOrderFulfillmentInput(
                            OrderFulfillmentMethod.Delivery,
                            zone.Id,
                            new PlaceOrderAddressInput("123 Main St", null, "City", null, null, null))),
                    null,
                    CancellationToken.None));
        }, platformContext: true);
    }

    [Fact]
    public async Task ZoneModificationsAndArchive_AfterOrderPlacement_LeaveHistoricalSnapshotUnchanged()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);
        var zone = await _fixture.SeedDeliveryZoneAsync(tenant.TenantId, "Original Zone", fee: 4.00m, minimumOrderSubtotal: 10.00m);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Coffee", basePrice: 15.00m);

        PlaceOrderResult result = null!;
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            result = await placement.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                    [new PlaceOrderItemInput(productId, null, 1)],
                    Fulfillment: new PlaceOrderFulfillmentInput(
                        OrderFulfillmentMethod.Delivery,
                        zone.Id,
                        new PlaceOrderAddressInput("100 Main St", null, "City", null, null, null))),
                null,
                CancellationToken.None);
        }, platformContext: true);

        // Modify and archive the zone
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var z = await context.DeliveryZones.FindAsync(zone.Id);
            Assert.NotNull(z);
            z.Update("New Zone Name", 10.00m, 50.00m, 99, DateTimeOffset.UtcNow);
            z.Archive(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Turn off delivery feature entirely for tenant
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: false);

        // Order detail must remain completely intact with original frozen values
        var client = CatalogTestClient.For(_fixture, tenant);
        var orderResponse = await client.GetFromJsonAsync<OrderDetailResponse>($"/api/tenant/orders/{result.OrderId}");

        Assert.NotNull(orderResponse);
        Assert.Equal(15.00m, orderResponse.Subtotal);
        Assert.Equal(4.00m, orderResponse.FulfillmentFee);
        Assert.Equal(19.00m, orderResponse.Total);
        Assert.NotNull(orderResponse.Fulfillment);
        Assert.Equal("Delivery", orderResponse.Fulfillment.Method);
        Assert.Equal(4.00m, orderResponse.Fulfillment.Fee);
        Assert.NotNull(orderResponse.Fulfillment.Delivery);
        Assert.Equal("Original Zone", orderResponse.Fulfillment.Delivery.ZoneName);
        Assert.Equal("100 Main St", orderResponse.Fulfillment.Delivery.AddressLine1);
    }

    [Fact]
    public async Task PostgresConstraint_DeletingOrder_CascadesToOrderFulfillmentSnapshot()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);
        var zone = await _fixture.SeedDeliveryZoneAsync(tenant.TenantId, "Zone A", fee: 3.00m);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Drink", basePrice: 10.00m);

        PlaceOrderResult result = null!;
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            result = await placement.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                    [new PlaceOrderItemInput(productId, null, 1)],
                    Fulfillment: new PlaceOrderFulfillmentInput(
                        OrderFulfillmentMethod.Delivery,
                        zone.Id,
                        new PlaceOrderAddressInput("100 Main St", null, "City", null, null, null))),
                null,
                CancellationToken.None);
        }, platformContext: true);

        // Verify snapshot exists
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var snapshotExists = await context.OrderFulfillmentSnapshots.AnyAsync(s => s.OrderId == result.OrderId);
            Assert.True(snapshotExists);

            // Delete order directly to verify PostgreSQL FK CASCADE
            var order = await context.Orders.FindAsync(result.OrderId);
            Assert.NotNull(order);
            context.Orders.Remove(order);
            await context.SaveChangesAsync();

            var snapshotAfterDelete = await context.OrderFulfillmentSnapshots.AnyAsync(s => s.OrderId == result.OrderId);
            Assert.False(snapshotAfterDelete);
        }, platformContext: true);
    }
}
