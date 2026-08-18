using System.Net;
using System.Net.Http.Json;
using Checkout.Domain;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Checkout;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Checkout;

[Collection(PostgresCollection.Name)]
public sealed class CheckoutConcurrencyTests
{
    private readonly PostgresApiFixture _fixture;

    public CheckoutConcurrencyTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task FiftyWayConcurrency_SameKeyAndPayload_CommitsExactlyOneOrderAndReplaysCleanly()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Beverages");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Espresso", basePrice: 3.50m);

        const int concurrency = 50;
        const string sharedKey = "concurrency-50-way-key-12345";

        var payload = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("Concurrency Tester", "+1234567890", "concurrency@example.com"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            using var client = _fixture.Factory.CreateClient();
            client.DefaultRequestHeaders.Host = tenant.Host;
            client.DefaultRequestHeaders.Add("Idempotency-Key", sharedKey);

            var response = await client.PostAsJsonAsync("/api/public/checkout/orders", payload);
            var order = await response.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();

            return (StatusCode: response.StatusCode, Order: order);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        // Every request must succeed with 201 Created (winner) or 200 OK (replays)
        foreach (var r in results)
        {
            Assert.True(r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.OK,
                $"Unexpected status code {r.StatusCode}");
            Assert.NotNull(r.Order);
        }

        // Exactly one request was 201 Created, others were 200 OK
        var createdCount = results.Count(r => r.StatusCode == HttpStatusCode.Created);
        var okCount = results.Count(r => r.StatusCode == HttpStatusCode.OK);

        Assert.Equal(1, createdCount);
        Assert.Equal(concurrency - 1, okCount);

        // Every single response returned the exact same OrderId and OrderNumber
        var firstOrderId = results[0].Order!.OrderId;
        var firstOrderNumber = results[0].Order!.OrderNumber;

        foreach (var r in results)
        {
            Assert.Equal(firstOrderId, r.Order!.OrderId);
            Assert.Equal(firstOrderNumber, r.Order!.OrderNumber);
        }

        // Verify database state: exactly 1 order and 1 idempotency record committed
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            var idempCount = await context.CheckoutIdempotencyRecords.CountAsync(r => r.TenantId == tenant.TenantId);

            Assert.Equal(1, orderCount);
            Assert.Equal(1, idempCount);
        }, platformContext: true);
    }

    [Fact]
    public async Task ConcurrentSameKeyDifferentPayload_AtMostOneOrderCommittedAndLosingReturnsConflict()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedOrderingTenantAsync(true, true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Bakery");
        var prod1 = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Muffin", basePrice: 3.00m);
        var prod2 = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Scone", basePrice: 3.50m);

        const string sharedKey = "concurrent-conflict-key-12345";

        var payload1 = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("User 1", "+1234567890", "u1@example.com"),
            [new CheckoutItemRequest(prod1, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var payload2 = new CheckoutSubmitRequest(
            new CheckoutGuestPartyRequest("User 2", "+1234567891", "u2@example.com"),
            [new CheckoutItemRequest(prod2, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        using var client1 = _fixture.Factory.CreateClient();
        client1.DefaultRequestHeaders.Host = tenant.Host;
        client1.DefaultRequestHeaders.Add("Idempotency-Key", sharedKey);

        using var client2 = _fixture.Factory.CreateClient();
        client2.DefaultRequestHeaders.Host = tenant.Host;
        client2.DefaultRequestHeaders.Add("Idempotency-Key", sharedKey);

        var task1 = client1.PostAsJsonAsync("/api/public/checkout/orders", payload1);
        var task2 = client2.PostAsJsonAsync("/api/public/checkout/orders", payload2);

        var responses = await Task.WhenAll(task1, task2);

        var statusCodes = responses.Select(r => r.StatusCode).ToArray();
        Assert.Contains(HttpStatusCode.Created, statusCodes);
        Assert.Contains(HttpStatusCode.Conflict, statusCodes);

        // Verify only 1 order exists in the database
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orderCount = await context.Orders.CountAsync(o => o.TenantId == tenant.TenantId);
            Assert.Equal(1, orderCount);
        }, platformContext: true);
    }

    [Fact]
    public async Task CheckoutIdempotencyRecords_DatabaseUniqueConstraint_Enforced()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId = Guid.NewGuid();
        var keyHash = new string('x', 64);
        var reqHash1 = new string('1', 64);
        var reqHash2 = new string('2', 64);

        var record1 = CheckoutIdempotencyRecord.CreateClaim(Guid.NewGuid(), tenantId, keyHash, reqHash1, DateTimeOffset.UtcNow);
        var record2 = CheckoutIdempotencyRecord.CreateClaim(Guid.NewGuid(), tenantId, keyHash, reqHash2, DateTimeOffset.UtcNow);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.CheckoutIdempotencyRecords.AddAsync(record1);
            await context.SaveChangesAsync();
        }, platformContext: true);

        // Attempting to insert duplicate (TenantId, KeyHash) must throw DbUpdateException
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, _) =>
            {
                await context.CheckoutIdempotencyRecords.AddAsync(record2);
                await context.SaveChangesAsync();
            }, platformContext: true);
        });
    }
}
