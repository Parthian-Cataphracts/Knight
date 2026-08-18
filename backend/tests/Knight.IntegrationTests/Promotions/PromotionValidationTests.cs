using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Ordering.Domain;
using Payment;
using Knight.Contracts.Checkout;
using Knight.Contracts.Payment;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Promotions;

/// <summary>
/// Validation coverage for the Promotions/Coupon engine: cross-tenant coupon
/// safety, quote-is-advisory semantics, idempotency fingerprint behaviour for
/// coupon codes, rollback guarantees, database-level tenant constraints, and
/// Payment's independence from Promotions.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PromotionValidationTests
{
    private const string PostgresUniqueViolation = "23505";
    private const string PostgresForeignKeyViolation = "23503";

    private readonly PostgresApiFixture _fixture;

    public PromotionValidationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Guid TenantId, string Host, Guid ProductId)> SetupStoreAsync(decimal productPrice = 100m)
    {
        var tenant = await _fixture.SeedPromotionsTenantAsync();
        await _fixture.SetOrderingFeatureAsync(tenant.TenantId, isEnabled: true);
        await _fixture.SetCatalogFeatureAsync(tenant.TenantId, isEnabled: true);
        await _fixture.SetDeliveryFeatureAsync(tenant.TenantId, isEnabled: true);
        await _fixture.SeedFulfillmentSettingsAsync(tenant.TenantId, pickupEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Pizza", basePrice: productPrice);

        return (tenant.TenantId, tenant.Host, productId);
    }

    private static CheckoutSubmitRequest Submit(Guid productId, string? couponCode, string guest = "Guest One", string method = "Pickup", Guid? zoneId = null) =>
        new(
            new CheckoutGuestPartyRequest(guest, "+1234567890", "guest@example.test"),
            [new CheckoutItemRequest(productId, null, 1, null)],
            new CheckoutFulfillmentRequest(
                method,
                zoneId,
                method == "Delivery" ? "1 Main St" : null,
                null,
                method == "Delivery" ? "Springfield" : null,
                method == "Delivery" ? "12345" : null,
                null,
                null),
            couponCode);

    private static Task<HttpResponseMessage> PostSubmitAsync(HttpClient client, string key, CheckoutSubmitRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "/api/public/checkout/orders")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.Add("Idempotency-Key", key);
        return client.SendAsync(message);
    }

    // ---------------------------------------------------------------------
    // §9 Cross-tenant coupon security
    // ---------------------------------------------------------------------

    /// <summary>
    /// A coupon belonging to another tenant must be indistinguishable from one that
    /// does not exist — no hint that the code is real, who owns it, which promotion
    /// it drives, or what state it is in.
    /// </summary>
    [Fact]
    public async Task CrossTenantCoupon_BehavesExactlyLikeUnknownCoupon_AndLeaksNothing()
    {
        if (!_fixture.IsAvailable) return;

        var storeA = await SetupStoreAsync();
        var storeB = await SetupStoreAsync();

        var promoB = await _fixture.SeedPromotionAsync(
            storeB.TenantId, "Tenant B Secret 40", global::Promotions.Domain.PromotionDiscountType.Percentage, 40m, requiresCoupon: true);
        await _fixture.SeedCouponAsync(storeB.TenantId, promoB, "TENANTBONLY");

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = storeA.Host;

        var quoteWithForeignCoupon = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(storeA.ProductId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null),
            "TENANTBONLY");

        var quoteWithUnknownCoupon = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(storeA.ProductId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null),
            "NEVEREXISTED");

        var foreignResponse = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteWithForeignCoupon);
        var unknownResponse = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteWithUnknownCoupon);

        Assert.Equal(unknownResponse.StatusCode, foreignResponse.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignResponse.StatusCode);

        var foreignBody = await foreignResponse.Content.ReadAsStringAsync();

        // Nothing about tenant B may surface.
        Assert.DoesNotContain("Tenant B Secret", foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(storeB.TenantId.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(promoB.ToString(), foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("archived", foreignBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("exists", foreignBody, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------
    // §20 Quote is advisory
    // ---------------------------------------------------------------------

    /// <summary>
    /// A quote reserves nothing. Submitting after the promotion changed must price
    /// against current server state, not against whatever the quote said.
    /// </summary>
    [Fact]
    public async Task Quote_DoesNotReserveEligibility_SubmitRepricesAgainstCurrentPromotion()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        var promoId = await _fixture.SeedPromotionAsync(
            store.TenantId, "Launch Offer", global::Promotions.Domain.PromotionDiscountType.Percentage, 10m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var quoteRequest = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(store.ProductId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var quote = await (await client.PostAsJsonAsync("/api/public/checkout/quote", quoteRequest))
            .Content.ReadFromJsonAsync<CheckoutQuoteResponse>();
        Assert.Equal(10m, quote!.DiscountTotal);
        Assert.Equal(90m, quote.Total);

        // Promotion is re-configured to 20% between quote and submit.
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE platform.promotions SET \"DiscountValue\" = 20 WHERE \"Id\" = {promoId}");
        }, platformContext: true);

        var submit = await PostSubmitAsync(client, $"advisory-{Guid.NewGuid():n}", Submit(store.ProductId, null));
        Assert.Equal(HttpStatusCode.Created, submit.StatusCode);

        var order = (await submit.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;
        Assert.Equal(20m, order.DiscountTotal);
        Assert.Equal(80m, order.Total);
    }

    /// <summary>
    /// §20 — a coupon quoted successfully but archived before submit must not be
    /// honoured; the quote conferred no entitlement.
    /// </summary>
    [Fact]
    public async Task Quote_WithCoupon_ThenCouponArchived_SubmitIsRejected()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        var promoId = await _fixture.SeedPromotionAsync(
            store.TenantId, "Coupon Only 25", global::Promotions.Domain.PromotionDiscountType.Percentage, 25m, requiresCoupon: true);
        var couponId = await _fixture.SeedCouponAsync(store.TenantId, promoId, "QUOTED25");

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var quoteRequest = new CheckoutQuoteRequest(
            [new CheckoutItemRequest(store.ProductId, null, 1, null)],
            new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null),
            "QUOTED25");

        var quoteResponse = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteRequest);
        Assert.Equal(HttpStatusCode.OK, quoteResponse.StatusCode);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var coupon = await context.Coupons.FirstAsync(c => c.Id == couponId);
            coupon.Archive(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, platformContext: true);

        var submit = await PostSubmitAsync(client, $"archived-{Guid.NewGuid():n}", Submit(store.ProductId, "QUOTED25"));
        Assert.Equal(HttpStatusCode.BadRequest, submit.StatusCode);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            Assert.Equal(0, await context.Orders.CountAsync(o => o.TenantId == store.TenantId));
        }, platformContext: true);
    }

    // ---------------------------------------------------------------------
    // §21 Coupon code participates in the request fingerprint
    // ---------------------------------------------------------------------

    [Fact]
    public async Task SameKey_DifferentCouponCode_Returns409AndCreatesNoSecondOrder()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        var promo10 = await _fixture.SeedPromotionAsync(
            store.TenantId, "Ten", global::Promotions.Domain.PromotionDiscountType.Percentage, 10m, requiresCoupon: true);
        var promo20 = await _fixture.SeedPromotionAsync(
            store.TenantId, "Twenty", global::Promotions.Domain.PromotionDiscountType.Percentage, 20m, requiresCoupon: true);
        await _fixture.SeedCouponAsync(store.TenantId, promo10, "SAVE10");
        await _fixture.SeedCouponAsync(store.TenantId, promo20, "SAVE20");

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var key = $"coupon-fingerprint-{Guid.NewGuid():n}";

        var first = await PostSubmitAsync(client, key, Submit(store.ProductId, "SAVE10"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        var second = await PostSubmitAsync(client, key, Submit(store.ProductId, "SAVE20"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            Assert.Equal(1, await context.Orders.CountAsync(o => o.TenantId == store.TenantId));
        }, platformContext: true);
    }

    /// <summary>
    /// §21 — coupon normalization in the fingerprint (trim + upper-case) mirrors
    /// <c>Coupon.NormalizeCode</c> exactly, so casing variants are the same semantic
    /// request and must replay rather than conflict.
    /// </summary>
    [Fact]
    public async Task SameKey_CouponCasingVariant_ReplaysOriginalOrder()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        var promoId = await _fixture.SeedPromotionAsync(
            store.TenantId, "Casing", global::Promotions.Domain.PromotionDiscountType.Percentage, 15m, requiresCoupon: true);
        await _fixture.SeedCouponAsync(store.TenantId, promoId, "SAVE15");

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var key = $"coupon-casing-{Guid.NewGuid():n}";

        var first = await PostSubmitAsync(client, key, Submit(store.ProductId, "SAVE15"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstOrder = (await first.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        var replay = await PostSubmitAsync(client, key, Submit(store.ProductId, "  save15  "));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayOrder = (await replay.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        Assert.Equal(firstOrder.OrderId, replayOrder.OrderId);
        Assert.Equal(firstOrder.OrderNumber, replayOrder.OrderNumber);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            Assert.Equal(1, await context.Orders.CountAsync(o => o.TenantId == store.TenantId));
            Assert.Equal(1, await context.CouponRedemptions.CountAsync(r => r.TenantId == store.TenantId));
        }, platformContext: true);
    }

    // ---------------------------------------------------------------------
    // §22 Replay after the promotion changed
    // ---------------------------------------------------------------------

    /// <summary>
    /// A replay returns the original committed order verbatim. It must not
    /// re-evaluate the (now different) promotion, and must not consume a second
    /// redemption.
    /// </summary>
    [Fact]
    public async Task Replay_AfterPromotionChanged_ReturnsOriginalOrderWithoutReevaluating()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        var promoId = await _fixture.SeedPromotionAsync(
            store.TenantId, "Replay 30", global::Promotions.Domain.PromotionDiscountType.Percentage, 30m, requiresCoupon: true);
        await _fixture.SeedCouponAsync(store.TenantId, promoId, "REPLAY30", usageLimitTotal: 5);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var key = $"replay-changed-{Guid.NewGuid():n}";

        var first = await PostSubmitAsync(client, key, Submit(store.ProductId, "REPLAY30"));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = (await first.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;
        Assert.Equal(30m, original.DiscountTotal);
        Assert.Equal(70m, original.Total);

        // Promotion is slashed and the coupon archived after the fact.
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE platform.promotions SET \"DiscountValue\" = 5, \"Name\" = 'Renamed' WHERE \"Id\" = {promoId}");
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE platform.coupons SET \"Status\" = 'Archived' WHERE \"TenantId\" = {store.TenantId}");
        }, platformContext: true);

        var replay = await PostSubmitAsync(client, key, Submit(store.ProductId, "REPLAY30"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = (await replay.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        Assert.Equal(original.OrderId, replayed.OrderId);
        Assert.Equal(original.OrderNumber, replayed.OrderNumber);
        Assert.Equal(30m, replayed.DiscountTotal);
        Assert.Equal(70m, replayed.Total);
        Assert.Equal("Replay 30", replayed.PromotionName);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            Assert.Equal(1, await context.Orders.CountAsync(o => o.TenantId == store.TenantId));
            Assert.Equal(1, await context.CouponRedemptions.CountAsync(r => r.TenantId == store.TenantId));
            Assert.Equal(1, await context.OrderPromotionSnapshots.CountAsync(s => s.TenantId == store.TenantId));
        }, platformContext: true);
    }

    // ---------------------------------------------------------------------
    // §28 Failed transaction rollback
    // ---------------------------------------------------------------------

    /// <summary>
    /// Fails the submission *after* promotion eligibility has been resolved (an
    /// unknown delivery zone is rejected by fulfillment resolution, which runs after
    /// promotion evaluation). Nothing may survive the rollback — not the order, the
    /// snapshot, the redemption, nor a completed idempotency claim.
    /// </summary>
    [Fact]
    public async Task FailureAfterPromotionResolution_RollsBackEverything_AndConsumesNoUsage()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        var promoId = await _fixture.SeedPromotionAsync(
            store.TenantId, "Rollback 50", global::Promotions.Domain.PromotionDiscountType.Percentage, 50m, requiresCoupon: true);
        var couponId = await _fixture.SeedCouponAsync(store.TenantId, promoId, "ROLLBACK50", usageLimitTotal: 1);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var response = await PostSubmitAsync(
            client,
            $"rollback-{Guid.NewGuid():n}",
            Submit(store.ProductId, "ROLLBACK50", method: "Delivery", zoneId: Guid.NewGuid()));

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound,
            $"Expected a client error, got {(int)response.StatusCode}.");

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            Assert.Equal(0, await context.Orders.CountAsync(o => o.TenantId == store.TenantId));
            Assert.Equal(0, await context.OrderPromotionSnapshots.CountAsync(s => s.TenantId == store.TenantId));
            Assert.Equal(0, await context.CouponRedemptions.CountAsync(r => r.TenantId == store.TenantId));
            Assert.Equal(0, await context.CheckoutIdempotencyRecords.CountAsync(
                r => r.TenantId == store.TenantId && r.CompletedAt != null));
        }, platformContext: true);

        // The coupon's single use is still available afterwards.
        var recovered = await PostSubmitAsync(
            client,
            $"rollback-ok-{Guid.NewGuid():n}",
            Submit(store.ProductId, "ROLLBACK50"));

        Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        var order = (await recovered.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;
        Assert.Equal(50m, order.DiscountTotal);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            Assert.Equal(1, await context.CouponRedemptions.CountAsync(r => r.TenantId == store.TenantId && r.CouponId == couponId));
        }, platformContext: true);
    }

    // ---------------------------------------------------------------------
    // §15 Delivery minimum evaluates the pre-discount subtotal
    // ---------------------------------------------------------------------

    /// <summary>
    /// Subtotal 100, promotion −30, delivery minimum 100. Delivery stays eligible
    /// because the minimum is measured on the pre-discount merchandise subtotal, and
    /// the delivery fee is added on top of the discounted subtotal rather than being
    /// discounted itself.
    /// </summary>
    [Fact]
    public async Task DeliveryMinimum_EvaluatesPreDiscountSubtotal_AndFeeIsNotDiscounted()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        await _fixture.SeedPromotionAsync(
            store.TenantId, "Thirty Off", global::Promotions.Domain.PromotionDiscountType.FixedAmount, 30m);

        var zone = await _fixture.SeedDeliveryZoneAsync(store.TenantId, "Zone100", fee: 10m, minimumOrderSubtotal: 100m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var response = await PostSubmitAsync(
            client,
            $"delivery-min-{Guid.NewGuid():n}",
            Submit(store.ProductId, null, method: "Delivery", zoneId: zone.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var order = (await response.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;
        Assert.Equal(100m, order.Subtotal);
        Assert.Equal(30m, order.DiscountTotal);
        Assert.Equal(70m, order.DiscountedSubtotal);
        Assert.Equal(10m, order.FulfillmentFee);
        Assert.Equal(80m, order.Total);
    }

    // ---------------------------------------------------------------------
    // §37 / §38 Payment independence
    // ---------------------------------------------------------------------

    /// <summary>
    /// Payment copies the order's final discounted total and nothing else. Changing
    /// or archiving the promotion afterwards must not move the amount.
    /// </summary>
    [Fact]
    public async Task Payment_UsesDiscountedOrderTotal_AndIsUnaffectedByLaterPromotionChanges()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        await _fixture.SetPaymentFeatureAsync(store.TenantId, isEnabled: true);

        var promoId = await _fixture.SeedPromotionAsync(
            store.TenantId, "Payment 20", global::Promotions.Domain.PromotionDiscountType.FixedAmount, 20m);
        var zone = await _fixture.SeedDeliveryZoneAsync(store.TenantId, "PayZone", fee: 10m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var response = await PostSubmitAsync(
            client,
            $"payment-promo-{Guid.NewGuid():n}",
            Submit(store.ProductId, null, method: "Delivery", zoneId: zone.Id));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        // Subtotal 100, discount 20, fee 10 => total 90.
        Assert.Equal(100m, order.Subtotal);
        Assert.Equal(20m, order.DiscountTotal);
        Assert.Equal(10m, order.FulfillmentFee);
        Assert.Equal(90m, order.Total);

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var payments = sp.GetRequiredService<IPaymentManagementService>();
            var created = await payments.CreatePaymentForOrderAsync(
                store.TenantId,
                new CreatePaymentRequest(order.OrderId, "PayOnFulfillment"),
                CancellationToken.None);

            Assert.Equal(90m, created.Amount);
            paymentId = created.Id;
        }, tenantId: store.TenantId);

        // Mutate and archive the promotion after the payment exists.
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE platform.promotions SET \"DiscountValue\" = 90, \"Status\" = 'Archived' WHERE \"Id\" = {promoId}");
        }, platformContext: true);

        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var payment = await context.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId);
            Assert.Equal(90m, payment.Amount);

            var persistedOrder = await context.Orders.AsNoTracking().FirstAsync(o => o.Id == order.OrderId);
            Assert.Equal(90m, persistedOrder.Total);
            Assert.Equal(20m, persistedOrder.DiscountTotal);
        }, platformContext: true);
    }

    // ---------------------------------------------------------------------
    // §45 Coupon listing performance and correctness
    // ---------------------------------------------------------------------

    /// <summary>
    /// The coupon list reports each coupon's redemption count. Those counts are
    /// resolved with a single grouped aggregate for the whole page rather than a
    /// count query per row, so this asserts the per-coupon attribution is still
    /// exact — including coupons with zero redemptions, which the aggregate omits.
    /// </summary>
    [Fact]
    public async Task CouponList_ReportsExactPerCouponRedemptionCounts()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedPromotionsTenantAsync();
        await _fixture.SetOrderingFeatureAsync(tenant.TenantId, isEnabled: true);
        await _fixture.SetCatalogFeatureAsync(tenant.TenantId, isEnabled: true);
        await _fixture.SeedFulfillmentSettingsAsync(tenant.TenantId, pickupEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenant.TenantId, "Food");
        var productId = await _fixture.SeedProductAsync(tenant.TenantId, categoryId, "Pizza", basePrice: 100m);

        var promoId = await _fixture.SeedPromotionAsync(
            tenant.TenantId, "List Counts", global::Promotions.Domain.PromotionDiscountType.Percentage, 10m, requiresCoupon: true);

        await _fixture.SeedCouponAsync(tenant.TenantId, promoId, "REDEEMEDTWICE", usageLimitTotal: 10);
        await _fixture.SeedCouponAsync(tenant.TenantId, promoId, "REDEEMEDONCE", usageLimitTotal: 10);
        await _fixture.SeedCouponAsync(tenant.TenantId, promoId, "NEVERUSED", usageLimitTotal: 10);

        using var storefront = _fixture.Factory.CreateClient();
        storefront.DefaultRequestHeaders.Host = tenant.Host;

        foreach (var code in new[] { "REDEEMEDTWICE", "REDEEMEDTWICE", "REDEEMEDONCE" })
        {
            var placed = await PostSubmitAsync(storefront, $"counts-{Guid.NewGuid():n}", Submit(productId, code));
            Assert.Equal(HttpStatusCode.Created, placed.StatusCode);
        }

        using var admin = _fixture.Factory.CreateClient();
        admin.DefaultRequestHeaders.Host = tenant.Host;
        admin.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tenant.Token);

        var listResponse = await admin.GetAsync("/api/tenant/coupons");
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);

        var payload = await listResponse.Content.ReadFromJsonAsync<CouponListPayload>();
        Assert.NotNull(payload);
        Assert.Equal(3, payload.TotalCount);

        var byCode = payload.Items.ToDictionary(c => c.Code, c => c.UsedCount);
        Assert.Equal(2, byCode["REDEEMEDTWICE"]);
        Assert.Equal(1, byCode["REDEEMEDONCE"]);
        Assert.Equal(0, byCode["NEVERUSED"]);
    }

    private sealed record CouponListPayload(
        IReadOnlyList<Knight.Contracts.Promotions.CouponResponse> Items,
        int TotalCount);

    // ---------------------------------------------------------------------
    // §30 / §31 / §32 Database-level tenant and cardinality constraints
    // ---------------------------------------------------------------------

    /// <summary>§30 — a coupon may not point at another tenant's promotion.</summary>
    [Fact]
    public async Task CouponReferencingForeignTenantPromotion_IsRejectedByCompositeForeignKey()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedPromotionsTenantAsync();
        var tenantB = await _fixture.SeedPromotionsTenantAsync();

        var promotionB = await _fixture.SeedPromotionAsync(tenantB.TenantId, "Tenant B Promo");

        var exception = await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, _) =>
            {
                var rogue = global::Promotions.Domain.Coupon.Create(
                    Guid.NewGuid(), tenantA.TenantId, promotionB, "ROGUECOUPON", null, null, null, DateTimeOffset.UtcNow);

                await context.Coupons.AddAsync(rogue);
                await context.SaveChangesAsync();
            }, platformContext: true);
        });

        Assert.Equal(PostgresForeignKeyViolation, ((PostgresException)exception.InnerException!).SqlState);
    }

    /// <summary>§31 — an order promotion snapshot may not attach to another tenant's order.</summary>
    [Fact]
    public async Task OrderPromotionSnapshotReferencingForeignTenantOrder_IsRejectedByCompositeForeignKey()
    {
        if (!_fixture.IsAvailable) return;

        var storeB = await SetupStoreAsync(100m);
        var tenantA = await _fixture.SeedPromotionsTenantAsync();

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = storeB.Host;

        var response = await PostSubmitAsync(client, $"fk-snapshot-{Guid.NewGuid():n}", Submit(storeB.ProductId, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var orderB = (await response.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        var exception = await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, _) =>
            {
                var rogue = OrderPromotionSnapshot.Create(
                    Guid.NewGuid(), tenantA.TenantId, orderB.OrderId, null, null,
                    "Cross Tenant", null, "Percentage", 10m, 10m, DateTimeOffset.UtcNow);

                await context.OrderPromotionSnapshots.AddAsync(rogue);
                await context.SaveChangesAsync();
            }, platformContext: true);
        });

        Assert.Equal(PostgresForeignKeyViolation, ((PostgresException)exception.InnerException!).SqlState);
    }

    /// <summary>§32 — at most one promotion snapshot may exist per order.</summary>
    [Fact]
    public async Task SecondOrderPromotionSnapshotForSameOrder_IsRejectedByUniqueConstraint()
    {
        if (!_fixture.IsAvailable) return;

        var store = await SetupStoreAsync(100m);
        await _fixture.SeedPromotionAsync(
            store.TenantId, "Snapshot Once", global::Promotions.Domain.PromotionDiscountType.Percentage, 10m);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = store.Host;

        var response = await PostSubmitAsync(client, $"one-snapshot-{Guid.NewGuid():n}", Submit(store.ProductId, null));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = (await response.Content.ReadFromJsonAsync<CheckoutSubmitResponse>())!;

        var exception = await Assert.ThrowsAnyAsync<DbUpdateException>(async () =>
        {
            await _fixture.WithScopeAsync(async (context, _) =>
            {
                var duplicate = OrderPromotionSnapshot.Create(
                    Guid.NewGuid(), store.TenantId, order.OrderId, null, null,
                    "Duplicate", null, "Percentage", 10m, 10m, DateTimeOffset.UtcNow);

                await context.OrderPromotionSnapshots.AddAsync(duplicate);
                await context.SaveChangesAsync();
            }, platformContext: true);
        });

        Assert.Equal(PostgresUniqueViolation, ((PostgresException)exception.InnerException!).SqlState);
    }
}
