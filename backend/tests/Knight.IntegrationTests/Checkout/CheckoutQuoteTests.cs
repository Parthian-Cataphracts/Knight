using System.Net;
using System.Net.Http.Json;
using Catalog.Domain;
using Delivery.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Checkout;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Checkout;

[Collection(PostgresCollection.Name)]
public sealed class CheckoutQuoteTests
{
    private readonly PostgresApiFixture _fixture;

    public CheckoutQuoteTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Quote_ValidPickupRequest_CalculatesPricesAndPersistsZeroRows()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Bakery");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Croissant", basePrice: 4.50m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        var quoteRequest = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(productId, null, 2, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var response = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var quote = await response.Content.ReadFromJsonAsync<CheckoutQuoteResponse>();
        Assert.NotNull(quote);
        Assert.Equal(9.00m, quote.Subtotal);
        Assert.Equal(0.00m, quote.FulfillmentFee);
        Assert.Equal(9.00m, quote.Total);
        Assert.Single(quote.Items);
        Assert.Equal("Croissant", quote.Items[0].ProductName);
        Assert.Equal(4.50m, quote.Items[0].UnitBasePrice);
        Assert.Equal(9.00m, quote.Items[0].LineTotal);

        // Verify quote persisted zero database rows
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            var snapshotCount = await context.OrderFulfillmentSnapshots.CountAsync(s => s.TenantId == tenant.TenantId);
            var idempCount = await context.CheckoutIdempotencyRecords.CountAsync(r => r.TenantId == tenant.TenantId);
            var counter = await context.TenantOrderCounters.FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId);

            Assert.Equal(0, orderCount);
            Assert.Equal(0, snapshotCount);
            Assert.Equal(0, idempCount);
            Assert.Null(counter); // Allocated 0 order numbers
        }, platformContext: true);
    }

    [Fact]
    public async Task Quote_ValidDeliveryRequest_CalculatesDeliveryFeeServerSide()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true);
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Pizzas");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Margherita", basePrice: 15.00m);

        var zoneId = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var zone = DeliveryZone.Create(zoneId, DateTimeOffset.UtcNow, tenant.TenantId, "North Zone", 5.00m, minimumOrderSubtotal: null, displayOrder: 0);
            await context.DeliveryZones.AddAsync(zone);
            await context.SaveChangesAsync();
        }, platformContext: true);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        var quoteRequest = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "100 Main St", null, "Metropolis", "10001", null, null));

        var response = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var quote = await response.Content.ReadFromJsonAsync<CheckoutQuoteResponse>();
        Assert.NotNull(quote);
        Assert.Equal(15.00m, quote.Subtotal);
        Assert.Equal(5.00m, quote.FulfillmentFee);
        Assert.Equal(20.00m, quote.Total);
    }

    [Fact]
    public async Task QuoteThenPriceChange_SubmitUsesUpdatedCatalogPrice()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Cafe");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Latte", basePrice: 4.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        // 1. Quote at 4.00
        var quoteRequest = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var quoteRes = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteRequest);
        Assert.Equal(HttpStatusCode.OK, quoteRes.StatusCode);
        var quote = await quoteRes.Content.ReadFromJsonAsync<CheckoutQuoteResponse>();
        Assert.Equal(4.00m, quote!.Total);

        // 2. Change product price in catalog to 6.00
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var prod = await context.Products.FirstAsync(p => p.Id == productId);
            prod.UpdateDetails("Latte", "latte", "Delicious", 6.00m, isVisible: true, isAvailable: true, displayOrder: 0, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // 3. Submit Checkout
        var submitRequest = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Alice", "+1234567890", "alice@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-price-change-key-1");
        var submitRes = await client.PostAsJsonAsync("/api/public/checkout/orders", submitRequest);
        Assert.Equal(HttpStatusCode.Created, submitRes.StatusCode);

        var order = await submitRes.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(order);
        Assert.Equal(6.00m, order.Subtotal);
        Assert.Equal(6.00m, order.Total);
    }

    [Fact]
    public async Task QuoteThenDeliveryFeeChange_SubmitUsesUpdatedDeliveryFee()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true);
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Burger", basePrice: 10.00m);

        var zoneId = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var zone = DeliveryZone.Create(zoneId, DateTimeOffset.UtcNow, tenant.TenantId, "South Zone", 3.00m, minimumOrderSubtotal: null, displayOrder: 0);
            await context.DeliveryZones.AddAsync(zone);
            await context.SaveChangesAsync();
        }, platformContext: true);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        // 1. Quote delivery fee 3.00
        var quoteRequest = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "123 Street", null, "City", "12345", null, null));

        var quoteRes = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteRequest);
        Assert.Equal(HttpStatusCode.OK, quoteRes.StatusCode);
        var quote = await quoteRes.Content.ReadFromJsonAsync<CheckoutQuoteResponse>();
        Assert.Equal(3.00m, quote!.FulfillmentFee);
        Assert.Equal(13.00m, quote.Total);

        // 2. Change zone fee to 8.00
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var zone = await context.DeliveryZones.FirstAsync(z => z.Id == zoneId);
            zone.Update("South Zone", 8.00m, null, 0, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // 3. Submit Checkout
        var submitRequest = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Bob", "+1234567891", "bob@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "123 Street", null, "City", "12345", null, null));

        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-fee-change-key-1");
        var submitRes = await client.PostAsJsonAsync("/api/public/checkout/orders", submitRequest);
        Assert.Equal(HttpStatusCode.Created, submitRes.StatusCode);

        var order = await submitRes.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(order);
        Assert.Equal(10.00m, order.Subtotal);
        Assert.Equal(8.00m, order.FulfillmentFee);
        Assert.Equal(18.00m, order.Total);
    }

    [Fact]
    public async Task QuoteThenProductUnavailable_SubmitFails_AndPersistsZeroOrders()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, catalogFeatureEnabled: true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Bakery");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Muffin", basePrice: 3.50m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        // 1. Quote succeeds
        var quoteReq = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var quoteRes = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteReq);
        Assert.Equal(HttpStatusCode.OK, quoteRes.StatusCode);

        // 2. Product becomes unavailable
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var prod = await context.Products.FirstAsync(p => p.Id == productId);
            prod.SetAvailability(false, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // 3. Submit Checkout fails
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-unavail-after-quote");
        var submitReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Guest", "+1234567890", "guest@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var submitRes = await client.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.BadRequest, submitRes.StatusCode);

        // Verify zero orders and zero idempotency records
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orders = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            var idemp = await context.CheckoutIdempotencyRecords.CountAsync(r => r.TenantId == tenant.TenantId);
            Assert.Equal(0, orders);
            Assert.Equal(0, idemp);
        }, platformContext: true);
    }

    [Fact]
    public async Task QuoteThenDeliveryBecomesInvalid_AcceptingOrdersOffOrZoneArchived_SubmitFails()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, catalogFeatureEnabled: true);
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Meals");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Curry", basePrice: 12.00m);

        var zoneId = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var zone = DeliveryZone.Create(zoneId, DateTimeOffset.UtcNow, tenant.TenantId, "Zone West", 4.00m, null, 0);
            await context.DeliveryZones.AddAsync(zone);
            await context.SaveChangesAsync();
        }, platformContext: true);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        // 1. Quote succeeds
        var quoteReq = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "500 Main St", null, "City", "12345", null, null));

        var quoteRes = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteReq);
        Assert.Equal(HttpStatusCode.OK, quoteRes.StatusCode);

        // Case A: IsAcceptingDeliveryOrders turned off -> submit fails
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var settings = await context.TenantDeliverySettings.FirstOrDefaultAsync(s => s.TenantId == tenant.TenantId);
            if (settings is null)
            {
                settings = TenantDeliverySettings.Create(tenant.TenantId, DateTimeOffset.UtcNow);
                settings.Update(isAcceptingDeliveryOrders: false, defaultMinimumOrderSubtotal: null, DateTimeOffset.UtcNow);
                await context.TenantDeliverySettings.AddAsync(settings);
            }
            else
            {
                settings.Update(isAcceptingDeliveryOrders: false, defaultMinimumOrderSubtotal: null, DateTimeOffset.UtcNow);
            }
            await context.SaveChangesAsync();
        }, platformContext: true);

        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-delivery-off-after-quote");
        var submitReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Guest", "+1234567890", "guest@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "500 Main St", null, "City", "12345", null, null));

        var submitRes1 = await client.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.BadRequest, submitRes1.StatusCode);

        // Case B: Turn delivery back on, but Archive Zone -> submit fails
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var settings = await context.TenantDeliverySettings.FirstAsync(s => s.TenantId == tenant.TenantId);
            settings.Update(isAcceptingDeliveryOrders: true, defaultMinimumOrderSubtotal: null, DateTimeOffset.UtcNow);

            var zone = await context.DeliveryZones.FirstAsync(z => z.Id == zoneId);
            zone.Archive(DateTimeOffset.UtcNow);

            await context.SaveChangesAsync();
        }, platformContext: true);

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-zone-archived-after-quote");
        var submitRes2 = await client.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.BadRequest, submitRes2.StatusCode);

        // Zero orders created
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orders = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            Assert.Equal(0, orders);
        }, platformContext: true);
    }
}
