using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Knight.Contracts.Common;
using Knight.Contracts.Promotions;
using Knight.IntegrationTests.Infrastructure;
using Xunit;

namespace Knight.IntegrationTests.Promotions;

[Collection(PostgresCollection.Name)]
public sealed class PromotionManagementTests
{
    private readonly PostgresApiFixture _fixture;

    public PromotionManagementTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Tenant_CanCreate_Update_Activate_AndArchive_Promotion()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPromotionsTenantAsync();
        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        client.DefaultRequestHeaders.Host = context.Host;

        // 1. Create promotion
        var createRequest = new CreatePromotionRequest(
            Name: "Summer Sale 20%",
            Description: "20% off all orders",
            DiscountType: "Percentage",
            DiscountValue: 20m,
            MinimumSubtotal: 50m,
            MaximumDiscountAmount: 30m,
            StartsAt: null,
            EndsAt: null,
            RequiresCoupon: false,
            Priority: 10);

        var createRes = await client.PostAsJsonAsync("/api/tenant/promotions", createRequest);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<PromotionResponse>();
        Assert.NotNull(created);
        Assert.Equal("Summer Sale 20%", created.Name);
        Assert.Equal("Draft", created.Status);
        Assert.Equal(20m, created.DiscountValue);

        // 2. Get by ID
        var getRes = await client.GetAsync($"/api/tenant/promotions/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, getRes.StatusCode);
        var fetched = await getRes.Content.ReadFromJsonAsync<PromotionResponse>();
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched.Id);

        // 3. Update promotion
        var updateRequest = new UpdatePromotionRequest(
            Name: "Summer Sale 25%",
            Description: "Updated description",
            DiscountType: "Percentage",
            DiscountValue: 25m,
            MinimumSubtotal: 40m,
            MaximumDiscountAmount: 35m,
            StartsAt: null,
            EndsAt: null,
            RequiresCoupon: false,
            Priority: 15);

        var updateRes = await client.PutAsJsonAsync($"/api/tenant/promotions/{created.Id}", updateRequest);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<PromotionResponse>();
        Assert.NotNull(updated);
        Assert.Equal("Summer Sale 25%", updated.Name);
        Assert.Equal(25m, updated.DiscountValue);

        // 4. Activate promotion
        var activateRes = await client.PostAsync($"/api/tenant/promotions/{created.Id}/activate", null);
        Assert.Equal(HttpStatusCode.OK, activateRes.StatusCode);
        var activated = await activateRes.Content.ReadFromJsonAsync<PromotionResponse>();
        Assert.NotNull(activated);
        Assert.Equal("Active", activated.Status);

        // 5. Archive promotion
        var archiveRes = await client.PostAsync($"/api/tenant/promotions/{created.Id}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archiveRes.StatusCode);
        var archived = await archiveRes.Content.ReadFromJsonAsync<PromotionResponse>();
        Assert.NotNull(archived);
        Assert.Equal("Archived", archived.Status);
    }

    [Fact]
    public async Task Tenant_CanCreate_Update_AndArchive_Coupon()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPromotionsTenantAsync();
        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        client.DefaultRequestHeaders.Host = context.Host;

        var promoId = await _fixture.SeedPromotionAsync(context.TenantId, "Coupon Promo", requiresCoupon: true);

        // 1. Create coupon
        var createRequest = new CreateCouponRequest(
            PromotionId: promoId,
            Code: "welcome10",
            UsageLimitTotal: 100,
            StartsAt: null,
            EndsAt: null);

        var createRes = await client.PostAsJsonAsync("/api/tenant/coupons", createRequest);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await createRes.Content.ReadFromJsonAsync<CouponResponse>();
        Assert.NotNull(created);
        Assert.Equal("welcome10", created.Code);
        Assert.Equal("WELCOME10", created.NormalizedCode);
        Assert.Equal(100, created.UsageLimitTotal);
        Assert.Equal(0, created.UsedCount);

        // 2. Duplicate normalized code in same tenant fails with 409 Conflict
        var dupRequest = new CreateCouponRequest(
            PromotionId: promoId,
            Code: "  WELCOME10  ",
            UsageLimitTotal: 50,
            StartsAt: null,
            EndsAt: null);

        var dupRes = await client.PostAsJsonAsync("/api/tenant/coupons", dupRequest);
        Assert.Equal(HttpStatusCode.Conflict, dupRes.StatusCode);

        // 3. Update coupon
        var updateRequest = new UpdateCouponRequest(
            UsageLimitTotal: 200,
            StartsAt: null,
            EndsAt: null);

        var updateRes = await client.PutAsJsonAsync($"/api/tenant/coupons/{created.Id}", updateRequest);
        Assert.Equal(HttpStatusCode.OK, updateRes.StatusCode);
        var updated = await updateRes.Content.ReadFromJsonAsync<CouponResponse>();
        Assert.NotNull(updated);
        Assert.Equal(200, updated.UsageLimitTotal);

        // 4. Archive coupon
        var archiveRes = await client.PostAsync($"/api/tenant/coupons/{created.Id}/archive", null);
        Assert.Equal(HttpStatusCode.OK, archiveRes.StatusCode);
        var archived = await archiveRes.Content.ReadFromJsonAsync<CouponResponse>();
        Assert.NotNull(archived);
        Assert.Equal("Archived", archived.Status);
    }

    [Fact]
    public async Task CrossTenant_SameCouponCode_IsAllowed()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedPromotionsTenantAsync();
        var tenantB = await _fixture.SeedPromotionsTenantAsync();

        var promoA = await _fixture.SeedPromotionAsync(tenantA.TenantId, "Promo A", requiresCoupon: true);
        var promoB = await _fixture.SeedPromotionAsync(tenantB.TenantId, "Promo B", requiresCoupon: true);

        var couponA = await _fixture.SeedCouponAsync(tenantA.TenantId, promoA, "GLOBAL10");
        var couponB = await _fixture.SeedCouponAsync(tenantB.TenantId, promoB, "GLOBAL10");

        Assert.NotEqual(couponA, couponB);
    }

    [Fact]
    public async Task PromotionsFeature_WhenDisabled_TenantEndpointsReturnForbidden()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPromotionsTenantAsync(promotionsFeatureEnabled: false);
        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        client.DefaultRequestHeaders.Host = context.Host;

        var res = await client.GetAsync("/api/tenant/promotions");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
