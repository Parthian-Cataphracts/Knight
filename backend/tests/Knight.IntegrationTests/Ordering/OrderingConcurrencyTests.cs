using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering;
using Ordering.Domain;
using Knight.Application.Exceptions;
using Knight.Infrastructure.Persistence;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Ordering;

[Collection(PostgresCollection.Name)]
public sealed class OrderingConcurrencyTests
{
    private readonly PostgresApiFixture _fixture;

    public OrderingConcurrencyTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task OrderNumberAllocation_HighConcurrency_AllocatesUniqueNumbersWithoutDuplicates()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync();
        var tenantB = await _fixture.SeedOrderingTenantAsync();

        const int iterationsPerTenant = 50;
        var allocatedNumbersA = new ConcurrentBag<long>();
        var allocatedNumbersB = new ConcurrentBag<long>();

        var tasksA = Enumerable.Range(0, iterationsPerTenant).Select(_ => Task.Run(async () =>
        {
            await _fixture.WithScopeAsync(async (_, sp) =>
            {
                var counterRepo = sp.GetRequiredService<ITenantOrderCounterRepository>();
                var number = await counterRepo.NextOrderNumberAsync(tenantA.TenantId, CancellationToken.None);
                allocatedNumbersA.Add(number);
            }, platformContext: true);
        }));

        var tasksB = Enumerable.Range(0, iterationsPerTenant).Select(_ => Task.Run(async () =>
        {
            await _fixture.WithScopeAsync(async (_, sp) =>
                {
                    var counterRepo = sp.GetRequiredService<ITenantOrderCounterRepository>();
                    var number = await counterRepo.NextOrderNumberAsync(tenantB.TenantId, CancellationToken.None);
                    allocatedNumbersB.Add(number);
                }, platformContext: true);
        }));

        await Task.WhenAll(tasksA.Concat(tasksB));

        // Verify Tenant A
        Assert.Equal(iterationsPerTenant, allocatedNumbersA.Count);
        var uniqueA = allocatedNumbersA.Distinct().ToList();
        Assert.Equal(iterationsPerTenant, uniqueA.Count); // Zero duplicates!

        // Verify Tenant B
        Assert.Equal(iterationsPerTenant, allocatedNumbersB.Count);
        var uniqueB = allocatedNumbersB.Distinct().ToList();
        Assert.Equal(iterationsPerTenant, uniqueB.Count); // Zero duplicates!
    }

    [Fact]
    public async Task ConcurrentStatusMutations_UsingStaleState_FailsWithConcurrencyConflict()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(
            orderingFeatureEnabled: true,
            catalogFeatureEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Snacks");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Chips", basePrice: 2.00m);

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

        // Load order in Scope 1 and Scope 2 simultaneously (same xmin)
        using var scope1 = _fixture.Factory.Services.CreateScope();
        var context1 = scope1.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var order1 = await context1.Orders.IgnoreQueryFilters().Include(o => o.StatusHistory).FirstAsync(o => o.Id == orderResult.OrderId);

        using var scope2 = _fixture.Factory.Services.CreateScope();
        var context2 = scope2.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var order2 = await context2.Orders.IgnoreQueryFilters().Include(o => o.StatusHistory).FirstAsync(o => o.Id == orderResult.OrderId);

        // Scope 1 confirms the order and saves
        order1.Confirm(DateTimeOffset.UtcNow);
        await context1.SaveChangesAsync();

        // Scope 2 attempts to cancel the order based on the stale snapshot
        order2.Cancel(DateTimeOffset.UtcNow, reason: "Stale cancel");

        // Scope 2 must fail due to PostgreSQL xmin row-version concurrency check
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => context2.SaveChangesAsync());

        // Verify final order state is Confirmed with valid history
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var finalOrder = await context.Orders
                .Include(o => o.StatusHistory)
                .FirstAsync(o => o.Id == orderResult.OrderId);

            Assert.Equal(OrderStatus.Confirmed, finalOrder.Status);
            Assert.Equal(2, finalOrder.StatusHistory.Count);
        }, platformContext: true);
    }
}
