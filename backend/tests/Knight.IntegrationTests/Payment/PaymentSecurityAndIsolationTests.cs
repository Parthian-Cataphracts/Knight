using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Domain;
using Payment;
using Payment.Domain;
using Knight.Contracts.Payment;
using Knight.IntegrationTests.Infrastructure;
using Xunit;
using PaymentEntity = global::Payment.Domain.Payment;

namespace Knight.IntegrationTests.Payment;

[Collection(PostgresCollection.Name)]
public sealed class PaymentSecurityAndIsolationTests
{
    private readonly PostgresApiFixture _fixture;

    public PaymentSecurityAndIsolationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Guid> SeedOrderAsync(Guid tenantId, decimal basePrice = 50.00m, string currency = "USD")
    {
        var categoryId = await _fixture.SeedCategoryAsync(tenantId, "Cat " + Guid.NewGuid());
        var productId = await _fixture.SeedProductAsync(tenantId, categoryId, "Prod " + Guid.NewGuid(), basePrice: basePrice);

        var orderId = Guid.NewGuid();
        var item = OrderItem.Create(
            Guid.NewGuid(),
            tenantId,
            orderId,
            productId,
            "Prod",
            null,
            null,
            basePrice,
            1,
            0);

        var order = Order.Create(
            orderId,
            DateTimeOffset.UtcNow,
            tenantId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            currency,
            [item]);

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            await db.Orders.AddAsync(order);
            await db.SaveChangesAsync();
        }, platformContext: true);

        return orderId;
    }

    [Fact]
    public async Task TenantA_CannotCreatePayment_ForTenantBOrder()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        var tenantB = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);

        // Seed Order in Tenant B
        var orderBId = await SeedOrderAsync(tenantB.TenantId, 50.00m, "USD");

        // Tenant A tries to create payment against Tenant B's order -> throws NotFoundException / 404
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            await Assert.ThrowsAsync<Knight.Application.Exceptions.NotFoundException>(() =>
                svc.CreatePaymentForOrderAsync(tenantA.TenantId, new CreatePaymentRequest(orderBId, "Online"), CancellationToken.None));
        }, tenantId: tenantA.TenantId);
    }

    [Fact]
    public async Task TenantA_CannotReadOrMutate_TenantBPayment()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        var tenantB = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);

        // Create Payment in Tenant B
        var orderBId = await SeedOrderAsync(tenantB.TenantId, 50.00m, "USD");
        Guid paymentBId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(tenantB.TenantId, new CreatePaymentRequest(orderBId, "PayOnFulfillment"), CancellationToken.None);
            paymentBId = res.Id;
        }, tenantId: tenantB.TenantId);

        // Tenant A tries to read Tenant B's payment -> 404 Not Found
        using var clientA = _fixture.Factory.CreateClient();
        clientA.DefaultRequestHeaders.Host = tenantA.Host;
        clientA.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tenantA.Token);

        var getRes = await clientA.GetAsync($"/api/tenant/payments/{paymentBId}");
        Assert.Equal(HttpStatusCode.NotFound, getRes.StatusCode);

        // Tenant A tries to mark-paid Tenant B's payment -> 404 Not Found
        var markRes = await clientA.PostAsJsonAsync($"/api/tenant/payments/{paymentBId}/mark-paid", new MarkPaymentPaidRequest());
        Assert.Equal(HttpStatusCode.NotFound, markRes.StatusCode);
    }

    [Fact]
    public async Task PaymentFeatureOff_PermissionOn_Returns403()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: false,
            permissions: [PaymentPermissions.View.Key]);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);

        var res = await client.GetAsync("/api/tenant/payments");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task PaymentFeatureOn_PermissionOff_Returns403()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: true,
            permissions: []);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);

        var res = await client.GetAsync("/api/tenant/payments");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task PaymentFeatureOn_PermissionOn_Returns200()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: true,
            permissions: [PaymentPermissions.View.Key]);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);

        var res = await client.GetAsync("/api/tenant/payments");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task PlatformAdmin_CanInspectPayments_EvenWhenFeatureOff()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: false);
        var adminToken = _fixture.CreatePlatformAdminToken();

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var res = await client.GetAsync($"/api/platform/tenants/{tenant.TenantId}/payments");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task NoPublicPaymentOrWebhookEndpointExists()
    {
        if (!_fixture.IsAvailable) return;

        using var client = _fixture.Factory.CreateClient();

        var resWebhook1 = await client.PostAsync("/api/payment/webhook", null);
        Assert.Equal(HttpStatusCode.NotFound, resWebhook1.StatusCode);

        var resWebhook2 = await client.PostAsync("/api/payments/callback", null);
        Assert.Equal(HttpStatusCode.NotFound, resWebhook2.StatusCode);

        var resPublic = await client.GetAsync($"/api/public/orders/{Guid.NewGuid()}/payment");
        Assert.Equal(HttpStatusCode.NotFound, resPublic.StatusCode);
    }

    [Fact]
    public async Task NoPaymentHardDeleteEndpointExists()
    {
        if (!_fixture.IsAvailable) return;

        var tenant = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = tenant.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tenant.Token);

        var resDelete = await client.DeleteAsync($"/api/tenant/payments/{Guid.NewGuid()}");
        Assert.True(resDelete.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task CrossTenantPaymentAttemptFK_ViolatesPostgresConstraint()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var payment = PaymentEntity.Create(paymentId, tenantId1, Guid.NewGuid(), 100m, "USD", PaymentMethod.Online, DateTimeOffset.UtcNow);
            await db.Payments.AddAsync(payment);
            await db.SaveChangesAsync();

            // Attempt with mismatched tenantId2 pointing to payment with tenantId1
            var badAttempt = PaymentAttempt.Create(Guid.NewGuid(), tenantId2, paymentId, 1, "test", DateTimeOffset.UtcNow);
            await db.PaymentAttempts.AddAsync(badAttempt);

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }, platformContext: true);
    }

    [Fact]
    public async Task CrossTenantPaymentStatusHistoryFK_ViolatesPostgresConstraint()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var payment = PaymentEntity.Create(paymentId, tenantId1, Guid.NewGuid(), 100m, "USD", PaymentMethod.Online, DateTimeOffset.UtcNow);
            await db.Payments.AddAsync(payment);
            await db.SaveChangesAsync();

            // History with mismatched tenantId2 pointing to payment with tenantId1
            var badHistory = PaymentStatusHistory.Create(Guid.NewGuid(), tenantId2, paymentId, PaymentStatus.Pending, PaymentStatus.Processing, DateTimeOffset.UtcNow, "System", null, null);
            await db.PaymentStatusHistories.AddAsync(badHistory);

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }, platformContext: true);
    }

    [Fact]
    public async Task DuplicateProviderReference_ViolatesPartialUniqueIndex()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId = Guid.NewGuid();
        var payment1 = PaymentEntity.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), 50m, "USD", PaymentMethod.Online, DateTimeOffset.UtcNow);
        var payment2 = PaymentEntity.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), 50m, "USD", PaymentMethod.Online, DateTimeOffset.UtcNow);

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            await db.Payments.AddRangeAsync(payment1, payment2);
            await db.SaveChangesAsync();

            var att1 = PaymentAttempt.Create(Guid.NewGuid(), tenantId, payment1.Id, 1, "provider-key", DateTimeOffset.UtcNow);
            att1.MarkSucceeded("shared-ref-12345", DateTimeOffset.UtcNow);

            var att2 = PaymentAttempt.Create(Guid.NewGuid(), tenantId, payment2.Id, 1, "provider-key", DateTimeOffset.UtcNow);
            att2.MarkSucceeded("shared-ref-12345", DateTimeOffset.UtcNow);

            await db.PaymentAttempts.AddRangeAsync(att1, att2);

            // Same tenant + same provider + same non-null reference -> violates unique constraint
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }, platformContext: true);
    }

    [Fact]
    public async Task CrossTenant_SameProviderReference_IsAllowed()
    {
        if (!_fixture.IsAvailable) return;

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var paymentA = PaymentEntity.Create(Guid.NewGuid(), tenantA, Guid.NewGuid(), 50m, "USD", PaymentMethod.Online, DateTimeOffset.UtcNow);
        var paymentB = PaymentEntity.Create(Guid.NewGuid(), tenantB, Guid.NewGuid(), 50m, "USD", PaymentMethod.Online, DateTimeOffset.UtcNow);

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            await db.Payments.AddRangeAsync(paymentA, paymentB);
            await db.SaveChangesAsync();

            var attA = PaymentAttempt.Create(Guid.NewGuid(), tenantA, paymentA.Id, 1, "provider-key", DateTimeOffset.UtcNow);
            attA.MarkSucceeded("shared-ref-12345", DateTimeOffset.UtcNow);

            var attB = PaymentAttempt.Create(Guid.NewGuid(), tenantB, paymentB.Id, 1, "provider-key", DateTimeOffset.UtcNow);
            attB.MarkSucceeded("shared-ref-12345", DateTimeOffset.UtcNow);

            await db.PaymentAttempts.AddRangeAsync(attA, attB);
            await db.SaveChangesAsync(); // Cross-tenant with same reference must succeed

            var count = await db.PaymentAttempts.CountAsync(a => a.ProviderReference == "shared-ref-12345");
            Assert.Equal(2, count);
        }, platformContext: true);
    }

    [Fact]
    public async Task MultipleAttempts_WithNullProviderReference_AreAllowed()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId = Guid.NewGuid();
        var payment = PaymentEntity.Create(Guid.NewGuid(), tenantId, Guid.NewGuid(), 50m, "USD", PaymentMethod.Online, DateTimeOffset.UtcNow);

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            await db.Payments.AddAsync(payment);
            await db.SaveChangesAsync();

            var att1 = PaymentAttempt.Create(Guid.NewGuid(), tenantId, payment.Id, 1, "provider-key", DateTimeOffset.UtcNow);
            var att2 = PaymentAttempt.Create(Guid.NewGuid(), tenantId, payment.Id, 2, "provider-key", DateTimeOffset.UtcNow);

            // Both have null ProviderReference
            await db.PaymentAttempts.AddRangeAsync(att1, att2);
            await db.SaveChangesAsync(); // Partial index allows multiple null references

            var count = await db.PaymentAttempts.CountAsync(a => a.PaymentId == payment.Id);
            Assert.Equal(2, count);
        }, platformContext: true);
    }
}
