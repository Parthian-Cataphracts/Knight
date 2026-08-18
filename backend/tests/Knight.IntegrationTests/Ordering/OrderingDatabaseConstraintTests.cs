using Microsoft.EntityFrameworkCore;
using Ordering.Domain;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Ordering;

[Collection(PostgresCollection.Name)]
public sealed class OrderingDatabaseConstraintTests
{
    private readonly PostgresApiFixture _fixture;

    public OrderingDatabaseConstraintTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CrossTenant_OrderItem_RejectedByPostgresCompositeForeignKey()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync();
        var tenantB = await _fixture.SeedOrderingTenantAsync();

        var itemB = OrderItem.Create(
            Guid.NewGuid(),
            tenantB.TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Coffee",
            null,
            null,
            4.00m,
            1,
            0);

        var orderB = Order.Create(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            tenantB.TenantId,
            1001,
            "USD",
            [itemB]);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.Orders.AddAsync(orderB);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Attempt: Insert OrderItem belonging to Tenant A, referencing Order B (Tenant B)
        var crossTenantItem = OrderItem.Create(
            Guid.NewGuid(),
            tenantA.TenantId, // Tenant A
            orderB.Id,         // Order from Tenant B!
            Guid.NewGuid(),
            "Malicious Item",
            null,
            null,
            5.00m,
            1,
            0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.OrderItems.AddAsync(crossTenantItem);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FK_order_items_orders_TenantId_OrderId", ex.InnerException?.Message ?? ex.Message);
        }, platformContext: true);
    }

    [Fact]
    public async Task CrossTenant_OrderItemModifier_RejectedByPostgresCompositeForeignKey()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync();
        var tenantB = await _fixture.SeedOrderingTenantAsync();

        var itemB = OrderItem.Create(
            Guid.NewGuid(),
            tenantB.TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Latte",
            null,
            null,
            4.00m,
            1,
            0);

        var orderB = Order.Create(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            tenantB.TenantId,
            1001,
            "USD",
            [itemB]);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.Orders.AddAsync(orderB);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Attempt: Insert OrderItemModifier belonging to Tenant A, referencing Item B (Tenant B)
        var crossTenantModifier = OrderItemModifier.Create(
            Guid.NewGuid(),
            tenantA.TenantId, // Tenant A
            itemB.Id,          // Item from Tenant B!
            Guid.NewGuid(),
            "Milk",
            Guid.NewGuid(),
            "Oat",
            1.00m,
            0);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.OrderItemModifiers.AddAsync(crossTenantModifier);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FK_order_item_modifiers_order_items_TenantId_OrderItemId", ex.InnerException?.Message ?? ex.Message);
        }, platformContext: true);
    }

    [Fact]
    public async Task CrossTenant_OrderStatusHistory_RejectedByPostgresCompositeForeignKey()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync();
        var tenantB = await _fixture.SeedOrderingTenantAsync();

        var itemB = OrderItem.Create(
            Guid.NewGuid(),
            tenantB.TenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Tea",
            null,
            null,
            3.00m,
            1,
            0);

        var orderB = Order.Create(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            tenantB.TenantId,
            1001,
            "USD",
            [itemB]);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.Orders.AddAsync(orderB);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Attempt: Insert OrderStatusHistory belonging to Tenant A, referencing Order B (Tenant B)
        var crossTenantHistory = OrderStatusHistory.Create(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            tenantA.TenantId, // Tenant A
            orderB.Id,         // Order from Tenant B!
            OrderStatus.Pending,
            OrderStatus.Confirmed);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.OrderStatusHistories.AddAsync(crossTenantHistory);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Contains("FK_order_status_history_orders_TenantId_OrderId", ex.InnerException?.Message ?? ex.Message);
        }, platformContext: true);
    }

    [Fact]
    public async Task OrderNumber_UniquePerTenant_DifferentTenantsCanShareSameOrderNumber()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync();
        var tenantB = await _fixture.SeedOrderingTenantAsync();

        var itemA = OrderItem.Create(Guid.NewGuid(), tenantA.TenantId, Guid.NewGuid(), Guid.NewGuid(), "A", null, null, 1m, 1, 0);
        var orderA = Order.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantA.TenantId, 1001, "USD", [itemA]);

        var itemB = OrderItem.Create(Guid.NewGuid(), tenantB.TenantId, Guid.NewGuid(), Guid.NewGuid(), "B", null, null, 1m, 1, 0);
        var orderB = Order.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantB.TenantId, 1001, "USD", [itemB]); // Same 1001 in Tenant B

        // Both orders with 1001 persist across different tenants
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.Orders.AddRangeAsync(orderA, orderB);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Duplicate 1001 in Tenant A fails
        var itemA2 = OrderItem.Create(Guid.NewGuid(), tenantA.TenantId, Guid.NewGuid(), Guid.NewGuid(), "A2", null, null, 1m, 1, 0);
        var duplicateOrderA = Order.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantA.TenantId, 1001, "USD", [itemA2]);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.Orders.AddAsync(duplicateOrderA);
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }, platformContext: true);
    }
}
