using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering;
using Ordering.Domain;
using Knight.Contracts.Common;
using Knight.Contracts.Ordering;
using Knight.IntegrationTests.Catalog;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Ordering;

[Collection(PostgresCollection.Name)]
public sealed class OrderingLifecycleAndStateTests
{
    private readonly PostgresApiFixture _fixture;

    public OrderingLifecycleAndStateTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Order> SeedPendingOrderAsync(Guid tenantId, Guid userId)
    {
        var categoryId = await _fixture.SeedCategoryAsync(tenantId, "Category " + Guid.NewGuid());
        var productId = await _fixture.SeedProductAsync(tenantId, categoryId, "Item " + Guid.NewGuid(), basePrice: 10.00m);

        PlaceOrderResult result = null!;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var placementService = sp.GetRequiredService<IOrderPlacementService>();
            result = await placementService.PlaceOrderAsync(
                tenantId,
                new PlaceOrderInput([new PlaceOrderItemInput(productId, null, 1)]),
                new OrderActorContext(userId, Knight.Application.Abstractions.Identity.PrincipalType.TenantUser),
                CancellationToken.None);
        }, platformContext: true);

        return await _fixture.WithScopeAsync(async (context, _) =>
        {
            return await context.Orders
                .Include(o => o.Items)
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.Id == result.OrderId);
        }, platformContext: true);
    }

    [Fact]
    public async Task LegalStatusTransitions_ThroughApi_SucceedsAndRecordsHistory()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true,
            permissions: PostgresApiFixture.AllOrderingPermissions());

        var order = await SeedPendingOrderAsync(tenant.TenantId, tenant.UserId);
        var client = CatalogTestClient.For(_fixture, tenant);

        // 1. Pending -> Confirmed
        var resp1 = await client.PostAsJsonAsync($"/api/tenant/orders/{order.Id}/status", new TransitionOrderStatusRequest
        {
            TargetStatus = "Confirmed"
        });
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var body1 = await resp1.Content.ReadFromJsonAsync<OrderDetailResponse>();
        Assert.NotNull(body1);
        Assert.Equal("Confirmed", body1.Status);
        Assert.Equal(2, body1.StatusHistory.Count);

        // 2. Confirmed -> Preparing
        var resp2 = await client.PostAsJsonAsync($"/api/tenant/orders/{order.Id}/status", new TransitionOrderStatusRequest
        {
            TargetStatus = "Preparing"
        });
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<OrderDetailResponse>();
        Assert.NotNull(body2);
        Assert.Equal("Preparing", body2.Status);
        Assert.Equal(3, body2.StatusHistory.Count);

        // 3. Preparing -> Ready
        var resp3 = await client.PostAsJsonAsync($"/api/tenant/orders/{order.Id}/status", new TransitionOrderStatusRequest
        {
            TargetStatus = "Ready"
        });
        Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);
        var body3 = await resp3.Content.ReadFromJsonAsync<OrderDetailResponse>();
        Assert.NotNull(body3);
        Assert.Equal("Ready", body3.Status);
        Assert.Equal(4, body3.StatusHistory.Count);

        // 4. Ready -> Completed
        var resp4 = await client.PostAsJsonAsync($"/api/tenant/orders/{order.Id}/status", new TransitionOrderStatusRequest
        {
            TargetStatus = "Completed"
        });
        Assert.Equal(HttpStatusCode.OK, resp4.StatusCode);
        var body4 = await resp4.Content.ReadFromJsonAsync<OrderDetailResponse>();
        Assert.NotNull(body4);
        Assert.Equal("Completed", body4.Status);
        Assert.NotNull(body4.CompletedAt);
        Assert.Equal(5, body4.StatusHistory.Count);

        // Verify audit log entries
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var auditEntries = await context.AuditLogEntries
                .Where(a => a.TenantId == tenant.TenantId && a.EntityId == order.Id.ToString())
                .ToListAsync();

            Assert.Contains(auditEntries, a => a.Action == "OrderPlaced");
            Assert.Contains(auditEntries, a => a.Action == "OrderConfirmed");
            Assert.Contains(auditEntries, a => a.Action == "OrderPreparing");
            Assert.Contains(auditEntries, a => a.Action == "OrderReady");
            Assert.Contains(auditEntries, a => a.Action == "OrderCompleted");
        }, platformContext: true);
    }

    [Fact]
    public async Task IllegalStatusTransitions_Return409Conflict()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true,
            permissions: PostgresApiFixture.AllOrderingPermissions());

        var order = await SeedPendingOrderAsync(tenant.TenantId, tenant.UserId);
        var client = CatalogTestClient.For(_fixture, tenant);

        // Attempt: Pending -> Ready directly
        var response = await client.PostAsJsonAsync($"/api/tenant/orders/{order.Id}/status", new TransitionOrderStatusRequest
        {
            TargetStatus = "Ready"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Verify no history row was added
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var savedOrder = await context.Orders
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.Id == order.Id);

            Assert.Equal(OrderStatus.Pending, savedOrder.Status);
            Assert.Single(savedOrder.StatusHistory);
        }, platformContext: true);
    }

    [Fact]
    public async Task CancelEndpoint_CancelsOrderAndSetsReason()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true,
            permissions: PostgresApiFixture.AllOrderingPermissions());

        var order = await SeedPendingOrderAsync(tenant.TenantId, tenant.UserId);
        var client = CatalogTestClient.For(_fixture, tenant);

        var cancelResp = await client.PostAsJsonAsync($"/api/tenant/orders/{order.Id}/cancel", new CancelOrderRequest
        {
            Reason = "Item out of stock"
        });

        Assert.Equal(HttpStatusCode.OK, cancelResp.StatusCode);
        var detail = await cancelResp.Content.ReadFromJsonAsync<OrderDetailResponse>();
        Assert.NotNull(detail);
        Assert.Equal("Cancelled", detail.Status);
        Assert.Equal("Item out of stock", detail.CancellationReason);
        Assert.NotNull(detail.CancelledAt);
        Assert.Equal(2, detail.StatusHistory.Count);

        var latestHistory = detail.StatusHistory.Last();
        Assert.Equal("Pending", latestHistory.FromStatus);
        Assert.Equal("Cancelled", latestHistory.ToStatus);
        Assert.Equal("Item out of stock", latestHistory.Reason);
    }

    [Fact]
    public async Task CompletedAndCancelled_AreTerminal_CannotTransitionOrCancel()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true,
            permissions: PostgresApiFixture.AllOrderingPermissions());

        var client = CatalogTestClient.For(_fixture, tenant);

        // Order 1 -> Completed
        var order1 = await SeedPendingOrderAsync(tenant.TenantId, tenant.UserId);
        await client.PostAsJsonAsync($"/api/tenant/orders/{order1.Id}/status", new TransitionOrderStatusRequest { TargetStatus = "Confirmed" });
        await client.PostAsJsonAsync($"/api/tenant/orders/{order1.Id}/status", new TransitionOrderStatusRequest { TargetStatus = "Preparing" });
        await client.PostAsJsonAsync($"/api/tenant/orders/{order1.Id}/status", new TransitionOrderStatusRequest { TargetStatus = "Ready" });
        await client.PostAsJsonAsync($"/api/tenant/orders/{order1.Id}/status", new TransitionOrderStatusRequest { TargetStatus = "Completed" });

        // Attempt cancel on completed order -> 409
        var cancelOnCompleted = await client.PostAsJsonAsync($"/api/tenant/orders/{order1.Id}/cancel", new CancelOrderRequest());
        Assert.Equal(HttpStatusCode.Conflict, cancelOnCompleted.StatusCode);

        // Order 2 -> Cancelled
        var order2 = await SeedPendingOrderAsync(tenant.TenantId, tenant.UserId);
        await client.PostAsJsonAsync($"/api/tenant/orders/{order2.Id}/cancel", new CancelOrderRequest { Reason = "Mistake" });

        // Attempt transition on cancelled order -> 409
        var transitionOnCancelled = await client.PostAsJsonAsync($"/api/tenant/orders/{order2.Id}/status", new TransitionOrderStatusRequest { TargetStatus = "Confirmed" });
        Assert.Equal(HttpStatusCode.Conflict, transitionOnCancelled.StatusCode);
    }
}
