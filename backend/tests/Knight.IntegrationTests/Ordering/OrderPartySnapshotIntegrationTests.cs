using System.Net;
using System.Net.Http.Json;
using Customer;
using Customer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering;
using Ordering.Domain;
using Knight.Application.Exceptions;
using Knight.Contracts.Ordering;
using Knight.IntegrationTests.Catalog;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.Ordering;

[Collection(PostgresCollection.Name)]
public sealed class OrderPartySnapshotIntegrationTests
{
    private readonly PostgresApiFixture _fixture;

    public OrderPartySnapshotIntegrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ExistingCustomer_OrderPlacement_SnapshotsServerSideCustomerData()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Espresso", basePrice: 4.50m);
        var customerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Seed active customer
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var customer = global::Customer.Domain.Customer.Create(
                customerId,
                now,
                tenant.TenantId,
                "Ali Reza",
                "+15551234567",
                "ali@example.com");
            await context.Customers.AddAsync(customer);

            await context.SaveChangesAsync();
        }, platformContext: true);

        // Place order with CustomerId
        PlaceOrderResult result = null!;
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            result = await placement.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                    [new PlaceOrderItemInput(productId, null, 2)],
                    CustomerId: customerId),
                null,
                CancellationToken.None);
        }, platformContext: true);

        Assert.NotNull(result);

        // Inspect via Tenant Order Detail API
        var client = CatalogTestClient.For(_fixture, tenant);
        var orderResponse = await client.GetFromJsonAsync<OrderDetailResponse>($"/api/tenant/orders/{result.OrderId}");

        Assert.NotNull(orderResponse);
        Assert.NotNull(orderResponse.Party);
        Assert.Equal(customerId, orderResponse.Party.SourceCustomerId);
        Assert.Equal("Ali Reza", orderResponse.Party.DisplayName);
        Assert.Equal("+15551234567", orderResponse.Party.Phone);
        Assert.Equal("ali@example.com", orderResponse.Party.Email);
    }

    [Fact]
    public async Task CustomerEditAndArchive_AfterOrder_LeavesHistoricalSnapshotUnchanged()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Tea", basePrice: 3.00m);
        var customerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Seed customer
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var customer = global::Customer.Domain.Customer.Create(
                customerId,
                now,
                tenant.TenantId,
                "Original Name",
                "+15551111111",
                "original@test.org");
            await context.Customers.AddAsync(customer);

            await context.SaveChangesAsync();
        }, platformContext: true);

        // Place order
        PlaceOrderResult orderResult = null!;
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            orderResult = await placement.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                    [new PlaceOrderItemInput(productId, null, 1)],
                    CustomerId: customerId),
                null,
                CancellationToken.None);
        }, platformContext: true);

        // Update customer details and then archive customer
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var customer = await context.Customers.FirstAsync(c => c.TenantId == tenant.TenantId && c.Id == customerId);
            customer.UpdateDetails("Modified Name", "+15559999999", "modified@test.org", DateTimeOffset.UtcNow);
            customer.Archive(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Read historical order detail via API
        var client = CatalogTestClient.For(_fixture, tenant);
        var orderResponse = await client.GetFromJsonAsync<OrderDetailResponse>($"/api/tenant/orders/{orderResult.OrderId}");

        Assert.NotNull(orderResponse);
        Assert.NotNull(orderResponse.Party);
        // Assert historical snapshot remains frozen with original data!
        Assert.Equal("Original Name", orderResponse.Party.DisplayName);
        Assert.Equal("+15551111111", orderResponse.Party.Phone);
        Assert.Equal("original@test.org", orderResponse.Party.Email);
    }

    [Fact]
    public async Task CrossTenantCustomer_OrderPlacement_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync();
        var tenantB = await _fixture.SeedOrderingTenantAsync();
        var categoryAId = await _fixture.SeedCategoryAsync(tenantA.TenantId, "Cat A");
        var productAId = await _fixture.SeedProductAsync(tenantA.TenantId, categoryAId, "Product A", basePrice: 10m);
        var customerBId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var customerB = global::Customer.Domain.Customer.Create(customerBId, now, tenantB.TenantId, "Customer B", "+15552222222", null);
            await context.Customers.AddAsync(customerB);

            await context.SaveChangesAsync();
        }, platformContext: true);

        await Assert.ThrowsAsync<ValidationException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, sp) =>
            {
                var placement = sp.GetRequiredService<IOrderPlacementService>();
                await placement.PlaceOrderAsync(
                    tenantA.TenantId,
                    new PlaceOrderInput(
                        [new PlaceOrderItemInput(productAId, null, 1)],
                        CustomerId: customerBId), // Belongs to Tenant B!
                    null,
                    CancellationToken.None);
            }, platformContext: true);
        });
    }

    [Fact]
    public async Task ArchivedCustomer_OrderPlacement_FailsValidation()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Cat");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Product", basePrice: 10m);
        var customerId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var customer = global::Customer.Domain.Customer.Create(customerId, now, tenant.TenantId, "Archived Customer", "+15553333333", null);
            customer.Archive(now);
            await context.Customers.AddAsync(customer);

            await context.SaveChangesAsync();
        }, platformContext: true);

        await Assert.ThrowsAsync<ValidationException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, sp) =>
            {
                var placement = sp.GetRequiredService<IOrderPlacementService>();
                await placement.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput(
                        [new PlaceOrderItemInput(productId, null, 1)],
                        CustomerId: customerId), // Archived customer!
                    null,
                    CancellationToken.None);
            }, platformContext: true);
        });
    }

    [Fact]
    public async Task GuestOrder_CreatesSnapshot_WithoutPersistingCustomerEntity()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Latte", basePrice: 5.50m);

        PlaceOrderResult orderResult = null!;
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            orderResult = await placement.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                    [new PlaceOrderItemInput(productId, null, 1)],
                    GuestParty: new PlaceOrderGuestPartyInput("Guest Buyer", "+15554443322", "guest@store.com")),
                null,
                CancellationToken.None);
        }, platformContext: true);

        // Verify no row was added to customers table
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var customerExists = await context.Customers.AnyAsync(c => c.TenantId == tenant.TenantId);
            Assert.False(customerExists, "Guest order should NOT create a persistent Customer entity.");
        }, platformContext: true);

        // Verify order detail returns guest snapshot
        var client = CatalogTestClient.For(_fixture, tenant);
        var orderResponse = await client.GetFromJsonAsync<OrderDetailResponse>($"/api/tenant/orders/{orderResult.OrderId}");

        Assert.NotNull(orderResponse);
        Assert.NotNull(orderResponse.Party);
        Assert.Null(orderResponse.Party.SourceCustomerId);
        Assert.Equal("Guest Buyer", orderResponse.Party.DisplayName);
        Assert.Equal("+15554443322", orderResponse.Party.Phone);
        Assert.Equal("guest@store.com", orderResponse.Party.Email);
    }

    [Fact]
    public async Task HistoricalOrderParty_RemainsReadable_WhenCustomerFeatureIsDisabled()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true,
            permissions: PostgresApiFixture.AllOrderingPermissions());

        // Ensure customers feature is disabled for this tenant
        await _fixture.SetCustomerFeatureAsync(tenant.TenantId, isEnabled: false);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Snack", basePrice: 6.00m);

        PlaceOrderResult orderResult = null!;
        await _fixture.WithScopeAsync(async (context, sp) =>
        {
            var placement = sp.GetRequiredService<IOrderPlacementService>();
            orderResult = await placement.PlaceOrderAsync(
                tenant.TenantId,
                new PlaceOrderInput(
                    [new PlaceOrderItemInput(productId, null, 1)],
                    GuestParty: new PlaceOrderGuestPartyInput("Guest Independent", "+15557778899", null)),
                null,
                CancellationToken.None);
        }, platformContext: true);

        // Read order through tenant order API (ordering feature enabled, customers feature disabled)
        var client = CatalogTestClient.For(_fixture, tenant);
        var orderResponse = await client.GetFromJsonAsync<OrderDetailResponse>($"/api/tenant/orders/{orderResult.OrderId}");

        Assert.NotNull(orderResponse);
        Assert.NotNull(orderResponse.Party);
        Assert.Equal("Guest Independent", orderResponse.Party.DisplayName);
    }

    [Fact]
    public async Task OrderPlacement_AtomicRollback_WithPartySnapshot_OnFailure()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Cat");
        var validProductId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Valid Product", basePrice: 10m);
        var nonExistentProductId = Guid.NewGuid();

        // Place multi-item order where second item fails validation
        await Assert.ThrowsAsync<ValidationException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, sp) =>
            {
                var placement = sp.GetRequiredService<IOrderPlacementService>();
                await placement.PlaceOrderAsync(
                    tenant.TenantId,
                    new PlaceOrderInput(
                        [
                            new PlaceOrderItemInput(validProductId, null, 1),
                            new PlaceOrderItemInput(nonExistentProductId, null, 1)
                        ],
                        GuestParty: new PlaceOrderGuestPartyInput("Rollback Guest", "+15556667788", null)),
                    null,
                    CancellationToken.None);
            }, platformContext: true);
        });

        // Ensure zero orders or party snapshots were persisted
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            var snapshotCount = await context.OrderPartySnapshots.CountAsync(p => p.TenantId == tenant.TenantId);

            Assert.Equal(0, orderCount);
            Assert.Equal(0, snapshotCount);
        }, platformContext: true);
    }
}
