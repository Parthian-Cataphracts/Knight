using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Checkout;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Checkout;

[Collection(PostgresCollection.Name)]
public sealed class CheckoutIdempotencyTests
{
    private readonly PostgresApiFixture _fixture;

    public CheckoutIdempotencyTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("short")] // < 8
    public async Task SubmitOrder_InvalidIdempotencyKey_Returns400(string? invalidKey)
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Snacks");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Chips", basePrice: 2.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        if (invalidKey is not null)
        {
            client.DefaultRequestHeaders.Add("Idempotency-Key", invalidKey);
        }

        var submitRequest = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Guest User", "+1234567890", "guest@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var response = await client.PostAsJsonAsync("/api/public/checkout/orders", submitRequest);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SubmitOrder_SameKeyAndSamePayload_ReturnsReplay200WithExactSameOrder()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Dessert");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Cake", basePrice: 8.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-replay-key-12345");

        var submitRequest = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Charlie", "+1234567890", "charlie@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        // First attempt -> 201 Created
        var firstResponse = await client.PostAsJsonAsync("/api/public/checkout/orders", submitRequest);
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        var firstOrder = await firstResponse.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(firstOrder);

        // Second attempt -> 200 OK with identical OrderId and OrderNumber
        var secondResponse = await client.PostAsJsonAsync("/api/public/checkout/orders", submitRequest);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        var secondOrder = await secondResponse.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(secondOrder);

        Assert.Equal(firstOrder.OrderId, secondOrder.OrderId);
        Assert.Equal(firstOrder.OrderNumber, secondOrder.OrderNumber);
        Assert.Equal(firstOrder.Total, secondOrder.Total);

        // Verify only 1 order and 1 audit record exist
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            var idempCount = await context.CheckoutIdempotencyRecords.CountAsync(r => r.TenantId == tenant.TenantId);
            var auditCount = await context.AuditLogEntries.CountAsync(a => a.TenantId == tenant.TenantId && a.Action == "OrderPlaced");

            Assert.Equal(1, orderCount);
            Assert.Equal(1, idempCount);
            Assert.Equal(1, auditCount); // Replay does not duplicate audit event
        }, platformContext: true);
    }

    [Fact]
    public async Task SubmitOrder_SameKeyDifferentPayload_Returns409Conflict()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Meals");
        var prod1 = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Pasta", basePrice: 12.00m);
        var prod2 = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Pizza", basePrice: 14.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-conflict-key-12345");

        var payload1 = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("David", "+1234567890", "david@example.com"),
            [new CheckoutItemRequest(prod1, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var payload2 = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("David", "+1234567890", "david@example.com"),
            [new CheckoutItemRequest(prod2, null, 1, null)], // Changed product!
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        // First attempt -> 201 Created
        var res1 = await client.PostAsJsonAsync("/api/public/checkout/orders", payload1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        // Second attempt with different payload -> 409 Conflict
        var res2 = await client.PostAsJsonAsync("/api/public/checkout/orders", payload2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);

        // Ensure only 1 order remains committed
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            Assert.Equal(1, orderCount);
        }, platformContext: true);
    }

    [Fact]
    public async Task SubmitOrder_SamePayloadDifferentKeys_CreatesTwoOrders()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Drinks");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Tea", basePrice: 3.00m);

        var payload = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Eva", "+1234567890", "eva@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        using var client1 = _fixture.Factory.CreateClient();
        client1.DefaultRequestHeaders.Host = tenant.Host;
        client1.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-key-order-aaa-1");

        var res1 = await client1.PostAsJsonAsync("/api/public/checkout/orders", payload);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        var order1 = await res1.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();

        using var client2 = _fixture.Factory.CreateClient();
        client2.DefaultRequestHeaders.Host = tenant.Host;
        client2.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-key-order-bbb-2");

        var res2 = await client2.PostAsJsonAsync("/api/public/checkout/orders", payload);
        Assert.Equal(HttpStatusCode.Created, res2.StatusCode);
        var order2 = await res2.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();

        Assert.NotNull(order1);
        Assert.NotNull(order2);
        Assert.NotEqual(order1.OrderId, order2.OrderId);
        Assert.NotEqual(order1.OrderNumber, order2.OrderNumber);
    }

    [Fact]
    public async Task SubmitOrder_SameKeyAcrossDifferentTenants_CreatesIndependentOrders()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync(true, true);
        var catA = await _fixture.SeedCategoryAsync(tenantA.TenantId, "Cat A");
        var prodA = await _fixture.SeedProductAsync(tenantA.TenantId, catA, "Product A", basePrice: 5.00m);

        var tenantB = await _fixture.SeedOrderingTenantAsync(true, true);
        var catB = await _fixture.SeedCategoryAsync(tenantB.TenantId, "Cat B");
        var prodB = await _fixture.SeedProductAsync(tenantB.TenantId, catB, "Product B", basePrice: 5.00m);

        const string sharedKey = "cross-tenant-shared-key-12345";

        using var clientA = _fixture.Factory.CreateClient();
        clientA.DefaultRequestHeaders.Host = tenantA.Host;
        clientA.DefaultRequestHeaders.Add("Idempotency-Key", sharedKey);

        var payloadA = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("User A", "+1234567890", "usera@example.com"),
            [new CheckoutItemRequest(prodA, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var resA = await clientA.PostAsJsonAsync("/api/public/checkout/orders", payloadA);
        Assert.Equal(HttpStatusCode.Created, resA.StatusCode);

        using var clientB = _fixture.Factory.CreateClient();
        clientB.DefaultRequestHeaders.Host = tenantB.Host;
        clientB.DefaultRequestHeaders.Add("Idempotency-Key", sharedKey);

        var payloadB = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("User B", "+1234567899", "userb@example.com"),
            [new CheckoutItemRequest(prodB, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var resB = await clientB.PostAsJsonAsync("/api/public/checkout/orders", payloadB);
        Assert.Equal(HttpStatusCode.Created, resB.StatusCode);

        // Verify both tenants have 1 order
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var countA = await context.Orders.CountAsync(o => o.TenantId == tenantA.TenantId);
            var countB = await context.Orders.CountAsync(o => o.TenantId == tenantB.TenantId);

            Assert.Equal(1, countA);
            Assert.Equal(1, countB);
        }, platformContext: true);
    }

    [Fact]
    public async Task SubmitOrder_FailedValidationRollsBack_KeyCanBeReusedAfterCorrection()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Bakery");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Bagel", basePrice: 2.50m);

        const string retryKey = "idemp-retry-after-fail-key";

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", retryKey);

        // 1. Submit with invalid quantity (0) -> 400 Bad Request
        var invalidPayload = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Frank", "+1234567890", "frank@example.com"),
            [new CheckoutItemRequest(productId, null, 0, null)], // Invalid!
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var failRes = await client.PostAsJsonAsync("/api/public/checkout/orders", invalidPayload);
        Assert.Equal(HttpStatusCode.BadRequest, failRes.StatusCode);

        // Verify 0 orders and 0 idempotency records committed
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            var idempCount = await context.CheckoutIdempotencyRecords.CountAsync(r => r.TenantId == tenant.TenantId);

            Assert.Equal(0, orderCount);
            Assert.Equal(0, idempCount);
        }, platformContext: true);

        // 2. Correct payload and resubmit using the same key -> 201 Created
        var validPayload = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Frank", "+1234567890", "frank@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var successRes = await client.PostAsJsonAsync("/api/public/checkout/orders", validPayload);
        Assert.Equal(HttpStatusCode.Created, successRes.StatusCode);

        var order = await successRes.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(order);
        Assert.Equal(2.50m, order.Total);
    }

    [Fact]
    public async Task SubmitOrder_SameKeyWithSubtleCoordinateDifference_Returns409Conflict()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Burger", basePrice: 10.00m);

        var zoneId = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var zone = global::Delivery.Domain.DeliveryZone.Create(zoneId, DateTimeOffset.UtcNow, tenant.TenantId, "Zone 1", 5.00m, null, 0);
            await context.DeliveryZones.AddAsync(zone);
            await context.SaveChangesAsync();
        }, platformContext: true);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-coordinate-diff-key-123");

        var payload1 = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Alice", "+1234567890", "alice@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "123 Main St", null, "City", "12345", 35.123451m, 51.123451m));

        var payload2 = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Alice", "+1234567890", "alice@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "123 Main St", null, "City", "12345", 35.123459m, 51.123451m));

        // First attempt -> 201 Created
        var res1 = await client.PostAsJsonAsync("/api/public/checkout/orders", payload1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);

        // Second attempt with slightly different coordinate under same key -> 409 Conflict
        var res2 = await client.PostAsJsonAsync("/api/public/checkout/orders", payload2);
        Assert.Equal(HttpStatusCode.Conflict, res2.StatusCode);
    }

    [Fact]
    public async Task SubmitOrder_ReplayAfterCatalogPriceChange_ReturnsOriginalSnapshotWithoutRepricing()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Specials");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Steak", basePrice: 20.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-price-change-replay-key");

        var payload = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Bob", "+1234567890", "bob@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        // 1. Initial order placed at $20.00 -> 201 Created
        var res1 = await client.PostAsJsonAsync("/api/public/checkout/orders", payload);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        var order1 = await res1.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(order1);
        Assert.Equal(20.00m, order1.Total);

        // 2. Change product price to $35.00 in catalog
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var product = await context.Products.FindAsync(productId);
            Assert.NotNull(product);
            product.UpdateDetails("Steak", "steak", "Updated description", 35.00m, isVisible: true, isAvailable: true, displayOrder: 0, DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // 3. Replay same key + original payload -> 200 OK with original $20.00 total (NOT repriced to $35.00)
        var res2 = await client.PostAsJsonAsync("/api/public/checkout/orders", payload);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var order2 = await res2.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(order2);
        Assert.Equal(order1.OrderId, order2.OrderId);
        Assert.Equal(order1.OrderNumber, order2.OrderNumber);
        Assert.Equal(20.00m, order2.Total);
    }

    [Fact]
    public async Task CheckoutIdempotencyRecords_SameKeyHashAcrossDifferentTenants_BothSucceedInPostgres()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var keyHash = new string('z', 64);
        var reqHash = new string('9', 64);

        var record1 = global::Checkout.Domain.CheckoutIdempotencyRecord.CreateClaim(Guid.NewGuid(), tenantId1, keyHash, reqHash, DateTimeOffset.UtcNow);
        var record2 = global::Checkout.Domain.CheckoutIdempotencyRecord.CreateClaim(Guid.NewGuid(), tenantId2, keyHash, reqHash, DateTimeOffset.UtcNow);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.CheckoutIdempotencyRecords.AddRangeAsync(record1, record2);
            await context.SaveChangesAsync();

            var count = await context.CheckoutIdempotencyRecords.CountAsync(r => r.KeyHash == keyHash);
            Assert.Equal(2, count);
        }, platformContext: true);
    }
}
