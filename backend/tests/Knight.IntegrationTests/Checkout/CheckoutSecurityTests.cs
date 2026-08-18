using System.Net;
using System.Net.Http.Json;
using Delivery.Domain;
using Fulfillment.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Checkout;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Checkout;

[Collection(PostgresCollection.Name)]
public sealed class CheckoutSecurityTests
{
    private readonly PostgresApiFixture _fixture;

    public CheckoutSecurityTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OrderingFeatureDisabled_QuoteAndSubmit_ReturnForbidden()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: false, catalogFeatureEnabled: true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Cat");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Item", basePrice: 5.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-ordering-disabled-key");

        var quoteReq = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var submitReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Guest", "+1234567890", "guest@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var quoteRes = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteReq);
        Assert.Equal(HttpStatusCode.Forbidden, quoteRes.StatusCode);

        var submitRes = await client.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.Forbidden, submitRes.StatusCode);
    }

    [Fact]
    public async Task CatalogFeatureDisabled_QuoteAndSubmit_ReturnForbidden()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, catalogFeatureEnabled: false);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Cat");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Item", basePrice: 5.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-catalog-disabled-key");

        var quoteReq = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var submitReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Guest", "+1234567890", "guest@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var quoteRes = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteReq);
        Assert.Equal(HttpStatusCode.Forbidden, quoteRes.StatusCode);

        var submitRes = await client.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.Forbidden, submitRes.StatusCode);
    }

    [Fact]
    public async Task CustomersFeatureDisabled_GuestCheckout_SucceedsWithoutCreatingCustomerRows()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, catalogFeatureEnabled: true);
        // Explicitly ensure 'customers' feature is disabled
        await _fixture.SetCustomerFeatureAsync(tenant.TenantId, isEnabled: false);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Main");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Soup", basePrice: 7.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-no-customers-feature-key");

        var submitReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Grace Hopper", "+1234567890", "grace@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var submitRes = await client.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.Created, submitRes.StatusCode);

        // Verify order created with OrderPartySnapshot, but 0 rows in customers table
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var customerCount = await context.Customers.CountAsync(c => c.TenantId == tenant.TenantId);
            var snapshot = await context.OrderPartySnapshots.FirstOrDefaultAsync(s => s.TenantId == tenant.TenantId);

            Assert.Equal(0, customerCount);
            Assert.NotNull(snapshot);
            Assert.Equal("Grace Hopper", snapshot.DisplayName);
            Assert.Null(snapshot.SourceCustomerId);
        }, platformContext: true);
    }

    [Fact]
    public async Task DeliveryFeatureDisabled_PickupSucceeds_DeliveryFailsWithBadRequest()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, catalogFeatureEnabled: true);
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: false);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Main");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Salad", basePrice: 6.00m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        // Pickup succeeds
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-pickup-when-delivery-off-key");
        var pickupReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Helen", "+1234567890", "helen@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var pickupRes = await client.PostAsJsonAsync("/api/public/checkout/orders", pickupReq);
        Assert.Equal(HttpStatusCode.Created, pickupRes.StatusCode);

        // Delivery fails with 400 BadRequest because delivery feature is disabled
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-delivery-when-delivery-off-key");
        var deliveryReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Helen", "+1234567890", "helen@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", Guid.NewGuid(), "123 Main", null, "City", "12345", null, null));

        var deliveryRes = await client.PostAsJsonAsync("/api/public/checkout/orders", deliveryReq);
        Assert.Equal(HttpStatusCode.BadRequest, deliveryRes.StatusCode);
    }

    [Fact]
    public async Task CrossTenantSelection_ProductOrZone_FailsSafelyWithoutInformationLeakage()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync(true, true);
        await _fixture.SetDeliveryFeatureAsync(tenantA.TenantId, isEnabled: true);

        var tenantB = await _fixture.SeedOrderingTenantAsync(true, true);
        await _fixture.SetDeliveryFeatureAsync(tenantB.TenantId, isEnabled: true);

        var catB = await _fixture.SeedCategoryAsync(tenantB.TenantId, "Cat B");
        var prodB = await _fixture.SeedProductAsync(tenantB.TenantId, catB, "Product B", basePrice: 10.00m);

        var zoneB = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var zb = DeliveryZone.Create(zoneB, DateTimeOffset.UtcNow, tenantB.TenantId, "Zone B", 5.00m, null, 0);
            await context.DeliveryZones.AddAsync(zb);
            await context.SaveChangesAsync();
        }, platformContext: true);

        using var clientA = _fixture.Factory.CreateClient();
        clientA.DefaultRequestHeaders.Host = tenantA.Host;
        clientA.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-cross-tenant-test-key");

        // Attempt to submit order in Tenant A using Tenant B's Product ID
        var crossProdReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Alice", "+1234567890", "alice@example.com"),
            [new CheckoutItemRequest(prodB, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var crossProdRes = await clientA.PostAsJsonAsync("/api/public/checkout/orders", crossProdReq);
        Assert.Equal(HttpStatusCode.BadRequest, crossProdRes.StatusCode);

        // Attempt to submit order in Tenant A using Tenant B's Delivery Zone ID
        var catA = await _fixture.SeedCategoryAsync(tenantA.TenantId, "Cat A");
        var prodA = await _fixture.SeedProductAsync(tenantA.TenantId, catA, "Product A", basePrice: 10.00m);

        clientA.DefaultRequestHeaders.Remove("Idempotency-Key");
        clientA.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-cross-tenant-zone-key");
        var crossZoneReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Alice", "+1234567890", "alice@example.com"),
            [new CheckoutItemRequest(prodA, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneB, "123 Main St", null, "City", "12345", null, null));

        var crossZoneRes = await clientA.PostAsJsonAsync("/api/public/checkout/orders", crossZoneReq);
        Assert.Equal(HttpStatusCode.BadRequest, crossZoneRes.StatusCode);
    }

    [Fact]
    public async Task UnknownHost_CheckoutRequest_FailsClosed()
    {
        if (!_fixture.IsAvailable) return;

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = "non-existent-tenant-domain.test";
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-unknown-host-key-123");

        var submitReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Unknown", "+1234567890", "unknown@example.com"),
            [new CheckoutItemRequest(Guid.NewGuid(), null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var response = await client.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SuspendedOrArchivedTenant_CheckoutRequest_FailsClosed()
    {
        if (!_fixture.IsAvailable) return;

        // 1. Suspended tenant
        var suspendedTenant = await _fixture.SeedOrderingTenantAsync(true, true);
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var t = await context.Tenants.FindAsync(suspendedTenant.TenantId);
            t!.Suspend(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        using var client1 = _fixture.Factory.CreateClient();
        client1.DefaultRequestHeaders.Host = suspendedTenant.Host;
        client1.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-suspended-tenant-key");

        var submitReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Test", "+1234567890", "test@example.com"),
            [new CheckoutItemRequest(Guid.NewGuid(), null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var resSuspended = await client1.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.Forbidden, resSuspended.StatusCode);

        // 2. Archived tenant
        var archivedTenant = await _fixture.SeedOrderingTenantAsync(true, true);
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var t = await context.Tenants.FindAsync(archivedTenant.TenantId);
            t!.Archive(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        using var client2 = _fixture.Factory.CreateClient();
        client2.DefaultRequestHeaders.Host = archivedTenant.Host;
        client2.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-archived-tenant-key");

        var resArchived = await client2.PostAsJsonAsync("/api/public/checkout/orders", submitReq);
        Assert.Equal(HttpStatusCode.Forbidden, resArchived.StatusCode);
    }

    [Fact]
    public async Task CheckoutSubmit_ExceedingLimit_Returns429()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Category");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product", basePrice: 5.00m);

        await using var lowLimitFactory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:CheckoutSubmitPermitLimit", "2");
        });

        var client = lowLimitFactory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        var request = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Rate Tester", "+1234567890", "ratetester@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            client.DefaultRequestHeaders.Remove("Idempotency-Key");
            client.DefaultRequestHeaders.Add("Idempotency-Key", $"rate-key-{i}-{Guid.NewGuid()}");
            last = await client.PostAsJsonAsync("/api/public/checkout/orders", request);
            if (last.StatusCode == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
    }

    [Fact]
    public async Task CheckoutSubmitRateLimit_IsPartitionedPerTenant_SoOneTenantCannotStarveAnother()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryA = await _fixture.SeedCategoryAsync(tenantA.TenantId, "Category A");
        var productA = await _fixture.SeedProductAsync(tenantA.TenantId, categoryA, "Product A", basePrice: 5.00m);

        var tenantB = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryB = await _fixture.SeedCategoryAsync(tenantB.TenantId, "Category B");
        var productB = await _fixture.SeedProductAsync(tenantB.TenantId, categoryB, "Product B", basePrice: 5.00m);

        await using var lowLimitFactory = _fixture.Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:CheckoutSubmitPermitLimit", "2");
        });

        // Both tenants are reached over the same in-process TestServer connection, so
        // the client IP component of the partition key is identical for both. Only the
        // tenant component can keep their budgets separate.
        var clientA = lowLimitFactory.CreateClient();
        clientA.DefaultRequestHeaders.Host = tenantA.Host;

        var requestA = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Tenant A Guest", "+1234567890", "a@example.com"),
            [new CheckoutItemRequest(productA, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        HttpResponseMessage? exhausted = null;
        for (var i = 0; i < 6; i++)
        {
            clientA.DefaultRequestHeaders.Remove("Idempotency-Key");
            clientA.DefaultRequestHeaders.Add("Idempotency-Key", $"tenant-a-key-{i}-{Guid.NewGuid()}");
            exhausted = await clientA.PostAsJsonAsync("/api/public/checkout/orders", requestA);
            if (exhausted.StatusCode == HttpStatusCode.TooManyRequests)
            {
                break;
            }
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted!.StatusCode);

        // Tenant B has spent none of its own budget and must still be served.
        var clientB = lowLimitFactory.CreateClient();
        clientB.DefaultRequestHeaders.Host = tenantB.Host;
        clientB.DefaultRequestHeaders.Add("Idempotency-Key", $"tenant-b-key-{Guid.NewGuid()}");

        var requestB = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Tenant B Guest", "+1234567890", "b@example.com"),
            [new CheckoutItemRequest(productB, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var responseB = await clientB.PostAsJsonAsync("/api/public/checkout/orders", requestB);

        Assert.NotEqual(HttpStatusCode.TooManyRequests, responseB.StatusCode);
    }

    [Fact]
    public async Task DeliveryOrder_SubtotalBelowMinimum_Fails_AndDoesNotCountDeliveryFeeTowardsMinimum()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var prod80 = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Item 80", basePrice: 80.00m);
        var prod100 = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Item 100", basePrice: 100.00m);

        var zoneId = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            // Zone minimum subtotal = 100.00, fee = 50.00
            var zone = DeliveryZone.Create(zoneId, DateTimeOffset.UtcNow, tenant.TenantId, "Zone 100Min", 50.00m, minimumOrderSubtotal: 100.00m, displayOrder: 0);
            await context.DeliveryZones.AddAsync(zone);
            await context.SaveChangesAsync();
        }, platformContext: true);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;

        // 1. Subtotal = 80.00 (Total with fee would be 130.00, but Subtotal 80 < Minimum 100) -> 400 BadRequest
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-min-fail-key-1");
        var failReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Guest", "+1234567890", "guest@example.com"),
            [new CheckoutItemRequest(prod80, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "123 Street", null, "City", "12345", null, null));

        var failRes = await client.PostAsJsonAsync("/api/public/checkout/orders", failReq);
        Assert.Equal(HttpStatusCode.BadRequest, failRes.StatusCode);

        // 2. Subtotal = 100.00 (Subtotal 100 >= Minimum 100) -> 201 Created
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "idemp-min-success-key-2");
        var successReq = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Guest", "+1234567890", "guest@example.com"),
            [new CheckoutItemRequest(prod100, null, 1, null)],
            new CheckoutFulfillmentRequest("Delivery", zoneId, "123 Street", null, "City", "12345", null, null));

        var successRes = await client.PostAsJsonAsync("/api/public/checkout/orders", successReq);
        Assert.Equal(HttpStatusCode.Created, successRes.StatusCode);
        var order = await successRes.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(order);
        Assert.Equal(100.00m, order.Subtotal);
        Assert.Equal(50.00m, order.FulfillmentFee);
        Assert.Equal(150.00m, order.Total);
    }
}
