using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Knight.Contracts.Checkout;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Checkout;

/// <summary>
/// Proves the Checkout idempotency invariant for modifier selections:
///
///   same RequestHash =&gt; same persisted Order semantics.
///
/// <c>CheckoutRequestHasher</c> sorts modifier ids, so [A,B] and [B,A] collapse to
/// one fingerprint. That is only sound if the two payloads genuinely cannot commit
/// different state, which requires <c>OrderItemModifier.DisplayOrder</c> to be
/// derived from Catalog rather than from the client's array order. These tests
/// exercise that against real PostgreSQL through the public storefront API.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CheckoutModifierOrderingTests
{
    private readonly PostgresApiFixture _fixture;

    public CheckoutModifierOrderingTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Seeds a product carrying two modifier groups whose Catalog positions are the
    /// deliberate inverse of the ids' natural sort, so a passing assertion cannot be
    /// explained by an accidental id-ordering coincidence.
    ///
    /// Catalog layout (assignment order, then modifier order within group):
    ///   group "Milk"  (assignment 0) -> "Oat" (0), "Soy" (1)
    ///   group "Syrup" (assignment 1) -> "Caramel" (0), "Vanilla" (1)
    /// </summary>
    private async Task<ModifierFixture> SeedProductWithOrderedModifiersAsync()
    {
        var tenant = await _fixture.SeedOrderingTenantAsync(orderingFeatureEnabled: true, catalogFeatureEnabled: true);
        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Drinks");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Latte", basePrice: 4.00m);

        var now = DateTimeOffset.UtcNow;

        var milkGroup = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), now, tenant.TenantId, "Milk", isRequired: false, minSelections: 0, maxSelections: 2, displayOrder: 0);
        var syrupGroup = global::Catalog.Domain.ModifierGroup.Create(
            Guid.NewGuid(), now, tenant.TenantId, "Syrup", isRequired: false, minSelections: 0, maxSelections: 2, displayOrder: 1);

        var oat = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), now, tenant.TenantId, milkGroup.Id, "Oat", priceDelta: 0.50m, isAvailable: true, displayOrder: 0);
        var soy = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), now, tenant.TenantId, milkGroup.Id, "Soy", priceDelta: 0.60m, isAvailable: true, displayOrder: 1);
        var caramel = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), now, tenant.TenantId, syrupGroup.Id, "Caramel", priceDelta: 0.75m, isAvailable: true, displayOrder: 0);
        var vanilla = global::Catalog.Domain.Modifier.Create(
            Guid.NewGuid(), now, tenant.TenantId, syrupGroup.Id, "Vanilla", priceDelta: 0.80m, isAvailable: true, displayOrder: 1);

        var milkAssignment = global::Catalog.Domain.ProductModifierGroup.Create(
            Guid.NewGuid(), now, tenant.TenantId, productId, milkGroup.Id, 0);
        var syrupAssignment = global::Catalog.Domain.ProductModifierGroup.Create(
            Guid.NewGuid(), now, tenant.TenantId, productId, syrupGroup.Id, 1);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.ModifierGroups.AddRangeAsync(milkGroup, syrupGroup);
            await context.Modifiers.AddRangeAsync(oat, soy, caramel, vanilla);
            await context.ProductModifierGroups.AddRangeAsync(milkAssignment, syrupAssignment);
            await context.SaveChangesAsync();
        }, platformContext: true);

        return new ModifierFixture(
            tenant.TenantId,
            tenant.Host,
            productId,
            OatId: oat.Id,
            SoyId: soy.Id,
            CaramelId: caramel.Id,
            VanillaId: vanilla.Id);
    }

    private sealed record ModifierFixture(
        Guid TenantId,
        string Host,
        Guid ProductId,
        Guid OatId,
        Guid SoyId,
        Guid CaramelId,
        Guid VanillaId);

    private static CheckoutSubmitRequest BuildRequest(Guid productId, IEnumerable<Guid> modifierIds, string guestName) =>
        new(
            new CheckoutGuestPartyRequest(guestName, "+1234567890", "guest@example.com"),
            [new CheckoutItemRequest(productId, null, 1, modifierIds.ToArray())],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

    private async Task<IReadOnlyList<(int DisplayOrder, string GroupName, string ModifierName, decimal Delta, Guid GroupId)>>
        ReadModifierSnapshotAsync(Guid tenantId, Guid orderId)
    {
        List<(int, string, string, decimal, Guid)> result = [];

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var item = await context.OrderItems
                .AsNoTracking()
                .FirstAsync(i => i.TenantId == tenantId && i.OrderId == orderId);

            var modifiers = await context.OrderItemModifiers
                .AsNoTracking()
                .Where(m => m.TenantId == tenantId && m.OrderItemId == item.Id)
                .OrderBy(m => m.DisplayOrder)
                .ToListAsync();

            result = modifiers
                .Select(m => (m.DisplayOrder, m.ModifierGroupName, m.ModifierName, m.UnitPriceDelta, m.SourceModifierGroupId))
                .ToList();
        }, platformContext: true);

        return result;
    }

    /// <summary>
    /// §13 — two independent orders (distinct idempotency keys) submitted with the
    /// same modifier set in opposite array order must persist byte-identical
    /// modifier snapshots: same sequence, DisplayOrder, names, prices and group
    /// association.
    /// </summary>
    [Fact]
    public async Task ReversedModifierOrder_PersistsIdenticalSnapshotOrdering()
    {
        if (!_fixture.IsAvailable) return;

        var f = await SeedProductWithOrderedModifiersAsync();

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = f.Host;

        // Deliberately submitted "worst case": Vanilla (last group, last modifier)
        // first, Oat (first group, first modifier) last.
        var forward = BuildRequest(f.ProductId, [f.OatId, f.SoyId, f.CaramelId, f.VanillaId], "Forward Guest");
        var reversed = BuildRequest(f.ProductId, [f.VanillaId, f.CaramelId, f.SoyId, f.OatId], "Forward Guest");

        client.DefaultRequestHeaders.Add("Idempotency-Key", $"modorder-forward-{Guid.NewGuid()}");
        var forwardResponse = await client.PostAsJsonAsync("/api/public/checkout/orders", forward);
        Assert.Equal(HttpStatusCode.Created, forwardResponse.StatusCode);
        var forwardOrder = (await forwardResponse.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"modorder-reversed-{Guid.NewGuid()}");
        var reversedResponse = await client.PostAsJsonAsync("/api/public/checkout/orders", reversed);
        Assert.Equal(HttpStatusCode.Created, reversedResponse.StatusCode);
        var reversedOrder = (await reversedResponse.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        Assert.NotEqual(forwardOrder.OrderId, reversedOrder.OrderId);

        var forwardModifiers = await ReadModifierSnapshotAsync(f.TenantId, forwardOrder.OrderId);
        var reversedModifiers = await ReadModifierSnapshotAsync(f.TenantId, reversedOrder.OrderId);

        // Canonical Catalog order, independent of either submitted array order.
        Assert.Equal(
            [(0, "Milk", "Oat"), (1, "Milk", "Soy"), (2, "Syrup", "Caramel"), (3, "Syrup", "Vanilla")],
            forwardModifiers.Select(m => (m.DisplayOrder, m.GroupName, m.ModifierName)).ToArray());

        Assert.Equal(
            forwardModifiers.Select(m => (m.DisplayOrder, m.GroupName, m.ModifierName, m.Delta, m.GroupId)).ToArray(),
            reversedModifiers.Select(m => (m.DisplayOrder, m.GroupName, m.ModifierName, m.Delta, m.GroupId)).ToArray());

        // §18 — client ordering must not touch money.
        Assert.Equal(forwardOrder.Subtotal, reversedOrder.Subtotal);
        Assert.Equal(forwardOrder.Total, reversedOrder.Total);
        Assert.Equal(4.00m + 0.50m + 0.60m + 0.75m + 0.80m, forwardOrder.Total);
    }

    /// <summary>
    /// §14 — because reversed modifier order is semantically the same request, a
    /// replay under the same key must return the original order rather than
    /// conflicting, and must not create a second order or idempotency record.
    /// </summary>
    [Fact]
    public async Task SameKey_ReversedModifierOrder_ReplaysOriginalOrder()
    {
        if (!_fixture.IsAvailable) return;

        var f = await SeedProductWithOrderedModifiersAsync();

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = f.Host;

        var key = $"modorder-replay-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var first = await client.PostAsJsonAsync(
            "/api/public/checkout/orders",
            BuildRequest(f.ProductId, [f.OatId, f.CaramelId], "Replay Guest"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstOrder = (await first.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        var replay = await client.PostAsJsonAsync(
            "/api/public/checkout/orders",
            BuildRequest(f.ProductId, [f.CaramelId, f.OatId], "Replay Guest"));

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayOrder = (await replay.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        Assert.Equal(firstOrder.OrderId, replayOrder.OrderId);
        Assert.Equal(firstOrder.OrderNumber, replayOrder.OrderNumber);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var orderCount = await context.Orders.CountAsync(o => o.TenantId == f.TenantId);
            var recordCount = await context.CheckoutIdempotencyRecords.CountAsync(r => r.TenantId == f.TenantId);

            Assert.Equal(1, orderCount);
            Assert.Equal(1, recordCount);
        }, platformContext: true);
    }

    /// <summary>
    /// §15 — a genuinely different modifier set under the same key is a different
    /// request and must conflict, leaving exactly one order.
    /// </summary>
    [Fact]
    public async Task SameKey_DifferentModifierSet_ConflictsAndCreatesNoSecondOrder()
    {
        if (!_fixture.IsAvailable) return;

        var f = await SeedProductWithOrderedModifiersAsync();

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = f.Host;

        var key = $"modorder-conflict-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);

        var first = await client.PostAsJsonAsync(
            "/api/public/checkout/orders",
            BuildRequest(f.ProductId, [f.OatId, f.CaramelId], "Conflict Guest"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await client.PostAsJsonAsync(
            "/api/public/checkout/orders",
            BuildRequest(f.ProductId, [f.OatId, f.VanillaId], "Conflict Guest"));

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            Assert.Equal(1, await context.Orders.CountAsync(o => o.TenantId == f.TenantId));
        }, platformContext: true);
    }

    /// <summary>
    /// §16 — duplicate modifier ids are rejected outright rather than normalized, so
    /// they can never produce ambiguous pricing, counts, snapshots or fingerprints.
    /// </summary>
    [Fact]
    public async Task DuplicateModifierIds_AreRejectedWithValidationError()
    {
        if (!_fixture.IsAvailable) return;

        var f = await SeedProductWithOrderedModifiersAsync();

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = f.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"modorder-dup-{Guid.NewGuid()}");

        var response = await client.PostAsJsonAsync(
            "/api/public/checkout/orders",
            BuildRequest(f.ProductId, [f.OatId, f.OatId], "Duplicate Guest"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            Assert.Equal(0, await context.Orders.CountAsync(o => o.TenantId == f.TenantId));
        }, platformContext: true);
    }

    /// <summary>
    /// §10 — historical snapshots stay frozen: re-ordering the live Catalog after
    /// placement must not reorder or mutate an already-placed order's modifiers.
    /// </summary>
    [Fact]
    public async Task CatalogReordering_AfterPlacement_DoesNotMutateHistoricalSnapshot()
    {
        if (!_fixture.IsAvailable) return;

        var f = await SeedProductWithOrderedModifiersAsync();

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = f.Host;
        client.DefaultRequestHeaders.Add("Idempotency-Key", $"modorder-frozen-{Guid.NewGuid()}");

        var response = await client.PostAsJsonAsync(
            "/api/public/checkout/orders",
            BuildRequest(f.ProductId, [f.OatId, f.CaramelId], "Frozen Guest"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        var before = await ReadModifierSnapshotAsync(f.TenantId, order.OrderId);
        Assert.Equal(["Oat", "Caramel"], before.Select(m => m.ModifierName).ToArray());

        // Invert the Catalog: swap the two group assignment positions and rename a
        // modifier. Historical rows must be untouched.
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var assignments = await context.ProductModifierGroups
                .Where(a => a.TenantId == f.TenantId && a.ProductId == f.ProductId)
                .ToListAsync();

            foreach (var assignment in assignments)
            {
                await context.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE platform.product_modifier_groups SET \"DisplayOrder\" = {(assignment.DisplayOrder == 0 ? 1 : 0)} WHERE \"Id\" = {assignment.Id}");
            }

            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE platform.modifiers SET \"Name\" = 'Renamed Oat' WHERE \"Id\" = {f.OatId}");
        }, platformContext: true);

        var after = await ReadModifierSnapshotAsync(f.TenantId, order.OrderId);

        Assert.Equal(
            before.Select(m => (m.DisplayOrder, m.GroupName, m.ModifierName, m.Delta)).ToArray(),
            after.Select(m => (m.DisplayOrder, m.GroupName, m.ModifierName, m.Delta)).ToArray());
    }
}
