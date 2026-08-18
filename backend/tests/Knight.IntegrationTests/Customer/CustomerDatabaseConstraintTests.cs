using Microsoft.EntityFrameworkCore;
using Ordering.Domain;
using Knight.IntegrationTests.Infrastructure;

namespace Knight.IntegrationTests.Customer;

[Collection(PostgresCollection.Name)]
public sealed class CustomerDatabaseConstraintTests
{
    private readonly PostgresApiFixture _fixture;

    public CustomerDatabaseConstraintTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task CrossTenant_OrderPartySnapshot_RejectedByPostgresCompositeForeignKey()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedOrderingTenantAsync();
        var tenantB = await _fixture.SeedOrderingTenantAsync();

        var orderBId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Seed order for Tenant B
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var item = OrderItem.Create(
                Guid.NewGuid(),
                tenantB.TenantId,
                orderBId,
                Guid.NewGuid(),
                "Product B",
                null,
                null,
                10.00m,
                1,
                0,
                []);

            var orderB = Order.Create(
                orderBId,
                now,
                tenantB.TenantId,
                1001,
                "USD",
                [item]);

            await context.Orders.AddAsync(orderB);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Attempt to create OrderPartySnapshot for Tenant A pointing to Tenant B OrderId
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, _) =>
            {
                var crossTenantParty = OrderPartySnapshot.CreateFromGuest(
                    Guid.NewGuid(),
                    now,
                    tenantA.TenantId, // Tenant A
                    orderBId,         // Belongs to Tenant B!
                    "Hacker",
                    "+15550000000",
                    null);

                await context.OrderPartySnapshots.AddAsync(crossTenantParty);
                await context.SaveChangesAsync();
            }, platformContext: true);
        });
    }

    [Fact]
    public async Task Duplicate_OrderPartySnapshot_RejectedByPostgresUniqueConstraint()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync();
        var orderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Seed order with party snapshot
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var item = OrderItem.Create(
                Guid.NewGuid(),
                tenant.TenantId,
                orderId,
                Guid.NewGuid(),
                "Product",
                null,
                null,
                15.00m,
                1,
                0,
                []);

            var party1 = OrderPartySnapshot.CreateFromGuest(
                Guid.NewGuid(),
                now,
                tenant.TenantId,
                orderId,
                "First Party",
                "+15551111111",
                null);

            var order = Order.Create(
                orderId,
                now,
                tenant.TenantId,
                1001,
                "USD",
                [item],
                party: party1);

            await context.Orders.AddAsync(order);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Attempt to insert a second party snapshot for the exact same order
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, _) =>
            {
                var party2 = OrderPartySnapshot.CreateFromGuest(
                    Guid.NewGuid(),
                    now,
                    tenant.TenantId,
                    orderId, // Same order!
                    "Second Party",
                    "+15552222222",
                    null);

                await context.OrderPartySnapshots.AddAsync(party2);
                await context.SaveChangesAsync();
            }, platformContext: true);
        });
    }
}
