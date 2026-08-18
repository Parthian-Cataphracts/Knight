using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Domain;
using Knight.Contracts.Checkout;
using Knight.Contracts.Ordering;
using Knight.Infrastructure.Persistence;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Promotions;

[Collection(PostgresCollection.Name)]
public sealed class PromotionCheckoutIntegrationTests
{
    private readonly PostgresApiFixture _fixture;

    public PromotionCheckoutIntegrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Guid TenantId, string Host, Guid ProductId)> SetupStoreAsync(decimal productPrice = 100m)
    {
        var tenantContext = await _fixture.SeedPromotionsTenantAsync();
        await _fixture.SetOrderingFeatureAsync(tenantContext.TenantId, isEnabled: true);
        await _fixture.SetCatalogFeatureAsync(tenantContext.TenantId, isEnabled: true);
        await _fixture.SetDeliveryFeatureAsync(tenantContext.TenantId, isEnabled: true);
        await _fixture.SeedFulfillmentSettingsAsync(tenantContext.TenantId, pickupEnabled: true);

        var categoryId = await _fixture.SeedCategoryAsync(tenantContext.TenantId, "Food");
        var productId = await _fixture.SeedProductAsync(tenantContext.TenantId, categoryId, "Pizza", basePrice: productPrice);

        return (tenantContext.TenantId, tenantContext.Host, productId);
    }

    [Fact]
    public async Task CheckoutQuote_AppliesAutomaticPromotion_HighestDiscountWins()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, productId) = await SetupStoreAsync(100m);

        // Seed two automatic promotions: 10% and 25%
        await _fixture.SeedPromotionAsync(tenantId, "10% Off", discountValue: 10m, priority: 0);
        await _fixture.SeedPromotionAsync(tenantId, "25% Off", discountValue: 25m, priority: 0);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = host;

        var quoteRequest = new CheckoutQuoteRequest(
            Items: [new CheckoutItemRequest(productId, null, 1, null)],
            Fulfillment: new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var res = await client.PostAsJsonAsync("/api/public/checkout/quote", quoteRequest);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var quote = await res.Content.ReadFromJsonAsync<CheckoutQuoteResponse>();
        Assert.NotNull(quote);
        Assert.Equal(100m, quote.Subtotal);
        Assert.Equal(25m, quote.DiscountTotal);
        Assert.Equal(75m, quote.DiscountedSubtotal);
        Assert.Equal(75m, quote.Total);
        Assert.NotNull(quote.AppliedPromotion);
        Assert.Equal("25% Off", quote.AppliedPromotion.Name);
    }

    [Fact]
    public async Task CheckoutOrder_WithCoupon_CreatesOrderPromotionSnapshot_AndRedemption()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, productId) = await SetupStoreAsync(100m);

        var promoId = await _fixture.SeedPromotionAsync(
            tenantId,
            "VIP 30% Off",
            global::Promotions.Domain.PromotionDiscountType.Percentage,
            30m,
            requiresCoupon: true);

        var couponId = await _fixture.SeedCouponAsync(tenantId, promoId, "VIP30", usageLimitTotal: 50);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = host;

        var key = $"idemp-order-{Guid.NewGuid():n}";
        var submitRequest = new CheckoutSubmitRequest(
            GuestParty: new CheckoutGuestPartyRequest("Alice Smith", "+1234567890", "alice@example.test"),
            Items: [new CheckoutItemRequest(productId, null, 1, null)],
            Fulfillment: new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null),
            CouponCode: "  vip30  ");

        var requestMsg = new HttpRequestMessage(HttpMethod.Post, "/api/public/checkout/orders")
        {
            Content = JsonContent.Create(submitRequest)
        };
        requestMsg.Headers.Add("Idempotency-Key", key);

        var res = await client.SendAsync(requestMsg);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);

        var created = await res.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(created);
        Assert.Equal(100m, created.Subtotal);
        Assert.Equal(30m, created.DiscountTotal);
        Assert.Equal(70m, created.DiscountedSubtotal);
        Assert.Equal(70m, created.Total);
        Assert.Equal("VIP 30% Off", created.PromotionName);
        Assert.Equal("VIP30", created.CouponCode);

        // Verify database persistence of snapshot and redemption
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var order = await context.Orders
                .Include(o => o.Promotion)
                .FirstOrDefaultAsync(o => o.Id == created.OrderId);

            Assert.NotNull(order);
            Assert.Equal(30m, order.DiscountTotal);
            Assert.Equal(70m, order.DiscountedSubtotal);
            Assert.Equal(70m, order.Total);
            Assert.NotNull(order.Promotion);
            Assert.Equal("VIP 30% Off", order.Promotion.PromotionName);
            Assert.Equal("VIP30", order.Promotion.CouponCode);
            Assert.Equal(30m, order.Promotion.DiscountAmount);

            var redemption = await context.CouponRedemptions
                .FirstOrDefaultAsync(r => r.TenantId == tenantId && r.OrderId == created.OrderId);

            Assert.NotNull(redemption);
            Assert.Equal(couponId, redemption.CouponId);
        }, tenantId: tenantId);
    }

    [Fact]
    public async Task CheckoutOrder_IdempotencyReplay_DoesNotDuplicateRedemption()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, productId) = await SetupStoreAsync(100m);

        var promoId = await _fixture.SeedPromotionAsync(tenantId, "Promo", discountValue: 20m, requiresCoupon: true);
        var couponId = await _fixture.SeedCouponAsync(tenantId, promoId, "REPLAY20", usageLimitTotal: 10);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = host;

        var key = $"idemp-replay-{Guid.NewGuid():n}";
        var submitRequest = new CheckoutSubmitRequest(
            GuestParty: new CheckoutGuestPartyRequest("Bob Jones", "+1234567890", "bob@example.test"),
            Items: [new CheckoutItemRequest(productId, null, 1, null)],
            Fulfillment: new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null),
            CouponCode: "REPLAY20");

        // 1st request -> 201 Created
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/public/checkout/orders")
        {
            Content = JsonContent.Create(submitRequest)
        };
        req1.Headers.Add("Idempotency-Key", key);
        var res1 = await client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.Created, res1.StatusCode);
        var order1 = await res1.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();

        // 2nd request (exact replay) -> 200 OK
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/public/checkout/orders")
        {
            Content = JsonContent.Create(submitRequest)
        };
        req2.Headers.Add("Idempotency-Key", key);
        var res2 = await client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        var order2 = await res2.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();

        Assert.NotNull(order1);
        Assert.NotNull(order2);
        Assert.Equal(order1.OrderId, order2.OrderId);

        // Verify exactly 1 redemption record exists
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var count = await context.CouponRedemptions
                .CountAsync(r => r.TenantId == tenantId && r.CouponId == couponId);
            Assert.Equal(1, count);
        }, tenantId: tenantId);
    }

    [Fact]
    public async Task Order_DiscountSnapshot_IsImmutableWhenPromotionIsArchived()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, productId) = await SetupStoreAsync(100m);

        var promoId = await _fixture.SeedPromotionAsync(tenantId, "Immutable Promo", discountValue: 20m, requiresCoupon: false);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = host;

        var key = $"idemp-snap-{Guid.NewGuid():n}";
        var submitRequest = new CheckoutSubmitRequest(
            GuestParty: new CheckoutGuestPartyRequest("Charlie", "+1234567890", "charlie@example.test"),
            Items: [new CheckoutItemRequest(productId, null, 1, null)],
            Fulfillment: new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null));

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/public/checkout/orders")
        {
            Content = JsonContent.Create(submitRequest)
        };
        req.Headers.Add("Idempotency-Key", key);
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var order = await res.Content.ReadFromJsonAsync<CheckoutSubmitResponse>();
        Assert.NotNull(order);
        Assert.Equal(80m, order.Total);

        // Now archive the promotion in the database
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var promo = await context.Promotions.FindAsync(promoId);
            promo!.Archive(DateTimeOffset.UtcNow);
            await context.SaveChangesAsync();
        }, tenantId: tenantId);

        // Order in DB must remain unchanged with 20 discount and 80 total
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var dbOrder = await context.Orders
                .Include(o => o.Promotion)
                .FirstOrDefaultAsync(o => o.Id == order.OrderId);

            Assert.NotNull(dbOrder);
            Assert.Equal(20m, dbOrder.DiscountTotal);
            Assert.Equal(80m, dbOrder.Total);
            Assert.NotNull(dbOrder.Promotion);
            Assert.Equal("Immutable Promo", dbOrder.Promotion.PromotionName);
        }, tenantId: tenantId);
    }

    [Fact]
    public async Task Concurrency_25ConcurrentCheckouts_WithUsageLimit1_YieldsExactlyOneSuccess()
    {
        if (!_fixture.IsAvailable) return;

        var (tenantId, host, productId) = await SetupStoreAsync(100m);

        var promoId = await _fixture.SeedPromotionAsync(
            tenantId,
            "Limited Flash Promo",
            global::Promotions.Domain.PromotionDiscountType.FixedAmount,
            25m,
            requiresCoupon: true);

        var couponId = await _fixture.SeedCouponAsync(tenantId, promoId, "LIMITED1", usageLimitTotal: 1);

        const int concurrency = 25;
        var tasks = new Task<HttpResponseMessage>[concurrency];

        for (int i = 0; i < concurrency; i++)
        {
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                using var client = _fixture.Factory.CreateClient();
                client.DefaultRequestHeaders.Host = host;

                var req = new CheckoutSubmitRequest(
                    GuestParty: new CheckoutGuestPartyRequest($"Guest {index}", "+1234567890", $"guest{index}@test.com"),
                    Items: [new CheckoutItemRequest(productId, null, 1, null)],
                    Fulfillment: new CheckoutFulfillmentRequest("Pickup", null, null, null, null, null, null, null),
                    CouponCode: "LIMITED1");

                var msg = new HttpRequestMessage(HttpMethod.Post, "/api/public/checkout/orders")
                {
                    Content = JsonContent.Create(req)
                };
                msg.Headers.Add("Idempotency-Key", $"idemp-race-{Guid.NewGuid():n}");

                return await client.SendAsync(msg);
            });
        }

        var responses = await Task.WhenAll(tasks);

        var successCount = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
        var failureCount = responses.Count(r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity);

        Assert.Equal(1, successCount);
        Assert.Equal(concurrency - 1, failureCount);

        // Verify in PostgreSQL database: exactly 1 redemption record exists
        await _fixture.WithScopeAsync(async (context, _) =>
        {
            var redemptions = await context.CouponRedemptions
                .Where(r => r.TenantId == tenantId && r.CouponId == couponId)
                .ToListAsync();

            Assert.Single(redemptions);
        }, tenantId: tenantId);
    }
}
