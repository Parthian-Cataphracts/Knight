using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Domain;
using Payment;
using Payment.Domain;
using Knight.Application.Exceptions;
using Knight.Contracts.Common;
using Knight.Contracts.Payment;
using Knight.IntegrationTests.Infrastructure;
using Xunit;
using PaymentEntity = global::Payment.Domain.Payment;

namespace Knight.IntegrationTests.Payment;

[Collection(PostgresCollection.Name)]
public sealed class PaymentLifecycleTests
{
    private readonly PostgresApiFixture _fixture;

    public PaymentLifecycleTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Guid OrderId, Guid ProductId)> SeedOrderWithProductAsync(
        Guid tenantId,
        decimal basePrice,
        decimal fulfillmentFee = 0m,
        string currency = "USD")
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

        OrderFulfillmentSnapshot? fulfillment = null;
        if (fulfillmentFee > 0)
        {
            fulfillment = OrderFulfillmentSnapshot.CreateDelivery(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                tenantId,
                orderId,
                Guid.NewGuid(),
                "Zone 1",
                fulfillmentFee,
                "123 Main St",
                null,
                "City",
                "12345",
                null,
                null);
        }

        var order = Order.Create(
            orderId,
            DateTimeOffset.UtcNow,
            tenantId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            currency,
            [item],
            fulfillment: fulfillment);

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            await db.Orders.AddAsync(order);
            await db.SaveChangesAsync();
        }, platformContext: true);

        return (orderId, productId);
    }

    [Fact]
    public async Task Payment_AmountAndCurrencyCopiedFromOrder_RemainsImmutableWhenCatalogChanges()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        await _fixture.SetOrderingFeatureAsync(context.TenantId, isEnabled: true);
        await _fixture.SetCatalogFeatureAsync(context.TenantId, isEnabled: true);

        // 1. Create order with item ($100.00) + fulfillment fee ($20.00) = $120.00 USD
        var (orderId, productId) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 100.00m, fulfillmentFee: 20.00m, currency: "USD");

        // 2. Create Payment via application service
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(
                context.TenantId,
                new CreatePaymentRequest(orderId, "Online"),
                CancellationToken.None);

            Assert.Equal(120.00m, res.Amount);
            Assert.Equal("USD", res.Currency);
            Assert.Equal("Pending", res.Status);
        }, tenantId: context.TenantId);

        // 3. Change Catalog base price to $250.00
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var prod = await db.Products.FindAsync(productId);
            prod!.UpdateDetails("Prod Updated", "prod-updated", "Updated desc", 250.00m, isVisible: true, isAvailable: true, displayOrder: 0, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }, platformContext: true);

        // 4. Read payment via Tenant API -> Amount remains 120.00 USD
        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);

        var listRes = await client.GetAsync($"/api/tenant/payments?orderId={orderId}");
        Assert.Equal(HttpStatusCode.OK, listRes.StatusCode);
        var paged = await listRes.Content.ReadFromJsonAsync<PagedResponse<PaymentSummaryResponse>>();
        Assert.NotNull(paged);
        var paymentSummary = Assert.Single(paged.Items);
        Assert.Equal(120.00m, paymentSummary.Amount);
        Assert.Equal("USD", paymentSummary.Currency);
    }

    [Fact]
    public async Task Payment_AmountAndCurrencyCopiedFromOrder_RemainsImmutableWhenDeliveryFeeChanges()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        await _fixture.SetOrderingFeatureAsync(context.TenantId, isEnabled: true);

        var zoneId = Guid.NewGuid();
        var categoryId = await _fixture.SeedCategoryAsync(context.TenantId, "Cat " + Guid.NewGuid());
        var productId = await _fixture.SeedProductAsync(context.TenantId, categoryId, "Prod", basePrice: 50.00m);

        var orderId = Guid.NewGuid();
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var zone = global::Delivery.Domain.DeliveryZone.Create(zoneId, DateTimeOffset.UtcNow, context.TenantId, "Zone 1", 15.00m, null, 0);
            await db.DeliveryZones.AddAsync(zone);

            var item = OrderItem.Create(Guid.NewGuid(), context.TenantId, orderId, productId, "Prod", null, null, 50.00m, 1, 0);
            var fulfillment = OrderFulfillmentSnapshot.CreateDelivery(Guid.NewGuid(), DateTimeOffset.UtcNow, context.TenantId, orderId, zoneId, "Zone 1", 15.00m, "456 Oak", null, "City", "12345", null, null);
            var order = Order.Create(orderId, DateTimeOffset.UtcNow, context.TenantId, 1002, "EUR", [item], fulfillment: fulfillment);

            await db.Orders.AddAsync(order);
            await db.SaveChangesAsync();
        }, platformContext: true);

        // Create Payment
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(
                context.TenantId,
                new CreatePaymentRequest(orderId, "PayOnFulfillment"),
                CancellationToken.None);

            Assert.Equal(65.00m, res.Amount);
            Assert.Equal("EUR", res.Currency);
        }, tenantId: context.TenantId);

        // Modify DeliveryZone fee to $50.00
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var zone = await db.DeliveryZones.FindAsync(zoneId);
            zone!.Update("Zone 1", 50.00m, null, 0, DateTimeOffset.UtcNow);
            await db.SaveChangesAsync();
        }, platformContext: true);

        // Verify Payment amount is unchanged (65.00 EUR)
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var repo = sp.GetRequiredService<IPaymentRepository>();
            var payment = await repo.GetByOrderIdAsync(context.TenantId, orderId, CancellationToken.None);
            Assert.NotNull(payment);
            Assert.Equal(65.00m, payment.Amount);
            Assert.Equal("EUR", payment.Currency);
        }, tenantId: context.TenantId);
    }

    [Fact]
    public async Task PayOnFulfillment_CanBeManuallyMarkedPaidByAuthorizedStaff()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: true,
            permissions: [PaymentPermissions.View.Key, PaymentPermissions.StatusManage.Key]);

        var (orderId, _) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 30.00m, currency: "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(
                context.TenantId,
                new CreatePaymentRequest(orderId, "PayOnFulfillment"),
                CancellationToken.None);
            paymentId = res.Id;
        }, tenantId: context.TenantId);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);

        // Mark paid
        var markPaidReq = new MarkPaymentPaidRequest("Customer paid cash on delivery");
        var resPost = await client.PostAsJsonAsync($"/api/tenant/payments/{paymentId}/mark-paid", markPaidReq);
        Assert.Equal(HttpStatusCode.OK, resPost.StatusCode);

        var paymentDto = await resPost.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.NotNull(paymentDto);
        Assert.Equal("Succeeded", paymentDto.Status);
        Assert.NotNull(paymentDto.SucceededAt);

        // Verify history
        Assert.Equal(2, paymentDto.StatusHistories.Count);
        var initialHistory = paymentDto.StatusHistories.First();
        Assert.Equal("Pending", initialHistory.ToStatus);
        var history = paymentDto.StatusHistories.Last();
        Assert.Equal("Succeeded", history.ToStatus);
        Assert.Equal("TenantUser", history.ActorType);
        Assert.Equal(context.UserId, history.ActorId);

        // Verify Order status is NOT changed
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var order = await db.Orders.FindAsync(orderId);
            Assert.Equal(OrderStatus.Pending, order!.Status);
        }, platformContext: true);
    }

    [Fact]
    public async Task OnlinePayment_CannotBeManuallyMarkedPaid_ReturnsConflict()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: true,
            permissions: [PaymentPermissions.View.Key, PaymentPermissions.StatusManage.Key]);

        var (orderId, _) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 20.00m, currency: "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(
                context.TenantId,
                new CreatePaymentRequest(orderId, "Online"),
                CancellationToken.None);
            paymentId = res.Id;
        }, tenantId: context.TenantId);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);

        // Attempt manual mark paid on Online payment -> 409 Conflict
        var markPaidReq = new MarkPaymentPaidRequest("Attempted manual online override");
        var resPost = await client.PostAsJsonAsync($"/api/tenant/payments/{paymentId}/mark-paid", markPaidReq);
        Assert.Equal(HttpStatusCode.Conflict, resPost.StatusCode);
    }

    [Fact]
    public async Task SuccessfulPayment_IsTerminal_RejectsCancelOrFailure()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: true,
            permissions: [PaymentPermissions.View.Key, PaymentPermissions.StatusManage.Key]);

        var (orderId, _) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 10.00m, currency: "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(
                context.TenantId,
                new CreatePaymentRequest(orderId, "PayOnFulfillment"),
                CancellationToken.None);
            paymentId = res.Id;
            await svc.MarkPaidAsync(context.TenantId, paymentId, new MarkPaymentPaidRequest("Paid"), CancellationToken.None);
        }, tenantId: context.TenantId);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);

        // Cancel on Succeeded payment -> 409 Conflict
        var cancelRes = await client.PostAsJsonAsync($"/api/tenant/payments/{paymentId}/cancel", new CancelPaymentRequest());
        Assert.Equal(HttpStatusCode.Conflict, cancelRes.StatusCode);
    }

    [Fact]
    public async Task CancelledPayment_IsTerminal_DoesNotCancelUnderlyingOrder()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: true,
            permissions: [PaymentPermissions.View.Key, PaymentPermissions.StatusManage.Key]);

        var (orderId, _) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 10.00m, currency: "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(
                context.TenantId,
                new CreatePaymentRequest(orderId, "Online"),
                CancellationToken.None);
            paymentId = res.Id;
        }, tenantId: context.TenantId);

        using var client = _fixture.Factory.CreateClient();
        client.DefaultRequestHeaders.Host = context.Host;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);

        var cancelRes = await client.PostAsJsonAsync($"/api/tenant/payments/{paymentId}/cancel", new CancelPaymentRequest("User decided not to pay"));
        Assert.Equal(HttpStatusCode.OK, cancelRes.StatusCode);

        var cancelledDto = await cancelRes.Content.ReadFromJsonAsync<PaymentResponse>();
        Assert.NotNull(cancelledDto);
        Assert.Equal("Cancelled", cancelledDto.Status);
        Assert.NotNull(cancelledDto.CancelledAt);

        // Verify Order status is unchanged (Pending, not Cancelled)
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var order = await db.Orders.FindAsync(orderId);
            Assert.Equal(OrderStatus.Pending, order!.Status);
        }, platformContext: true);
    }

    [Fact]
    public async Task PaymentAttempt_FailedAttemptRemainsHistorical_RetryCreatesNewAttempt()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        var (orderId, _) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 10.00m, currency: "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(context.TenantId, new CreatePaymentRequest(orderId, "Online"), CancellationToken.None);
            paymentId = res.Id;

            // Start attempt 1
            var att1 = await svc.StartAttemptAsync(context.TenantId, paymentId, new StartPaymentAttemptRequest("provider-a"), CancellationToken.None);
            Assert.Equal(1, att1.AttemptNumber);

            // Fail attempt 1
            await svc.CompleteAttemptAsync(context.TenantId, paymentId, att1.AttemptId, new CompletePaymentAttemptRequest("Failed", "ref-1", "INSUFFICIENT_FUNDS", "Declined"), CancellationToken.None);

            // Retry -> Start attempt 2
            var att2 = await svc.StartAttemptAsync(context.TenantId, paymentId, new StartPaymentAttemptRequest("provider-a"), CancellationToken.None);
            Assert.Equal(2, att2.AttemptNumber);

            // Succeed attempt 2
            var paymentResult = await svc.CompleteAttemptAsync(context.TenantId, paymentId, att2.AttemptId, new CompletePaymentAttemptRequest("Succeeded", "ref-2"), CancellationToken.None);
            Assert.Equal("Succeeded", paymentResult.Status);
            Assert.Equal(2, paymentResult.Attempts.Count);

            // Verify Attempt 1 remains intact as historical failed
            var attempt1Dto = paymentResult.Attempts.First(a => a.AttemptNumber == 1);
            Assert.Equal("Failed", attempt1Dto.Status);
            Assert.Equal("INSUFFICIENT_FUNDS", attempt1Dto.FailureCode);

            // Verify Attempt 2 is succeeded
            var attempt2Dto = paymentResult.Attempts.First(a => a.AttemptNumber == 2);
            Assert.Equal("Succeeded", attempt2Dto.Status);
            Assert.Equal("ref-2", attempt2Dto.ProviderReference);
        }, tenantId: context.TenantId);
    }

    [Fact]
    public async Task SucceededPayment_IsTerminal_RejectsStartAttemptMarkPaidCancelAndSecondPayment()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: true,
            permissions: [PaymentPermissions.View.Key, PaymentPermissions.StatusManage.Key]);

        var (orderId, _) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 25.00m, currency: "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(context.TenantId, new CreatePaymentRequest(orderId, "Online"), CancellationToken.None);
            paymentId = res.Id;

            var attempt = await svc.StartAttemptAsync(context.TenantId, paymentId, new StartPaymentAttemptRequest("provider-1"), CancellationToken.None);
            await svc.CompleteAttemptAsync(context.TenantId, paymentId, attempt.AttemptId, new CompletePaymentAttemptRequest("Succeeded", "ref-ok"), CancellationToken.None);
        }, tenantId: context.TenantId);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();

            // 1. StartAttempt rejected with ConflictException
            await Assert.ThrowsAsync<ConflictException>(() =>
                svc.StartAttemptAsync(context.TenantId, paymentId, new StartPaymentAttemptRequest("provider-x"), CancellationToken.None));

            // 2. MarkPaid rejected with ConflictException
            await Assert.ThrowsAsync<ConflictException>(() =>
                svc.MarkPaidAsync(context.TenantId, paymentId, new MarkPaymentPaidRequest("Double pay"), CancellationToken.None));

            // 3. Cancel rejected with ConflictException
            await Assert.ThrowsAsync<ConflictException>(() =>
                svc.CancelPaymentAsync(context.TenantId, paymentId, new CancelPaymentRequest(), CancellationToken.None));

            // 4. Second payment for same order rejected with ConflictException
            await Assert.ThrowsAsync<ConflictException>(() =>
                svc.CreatePaymentForOrderAsync(context.TenantId, new CreatePaymentRequest(orderId, "Online"), CancellationToken.None));
        }, tenantId: context.TenantId);
    }

    [Fact]
    public async Task CancelledPayment_IsTerminal_RejectsStartAttemptMarkPaidAndSuccess()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(
            paymentFeatureEnabled: true,
            permissions: [PaymentPermissions.View.Key, PaymentPermissions.StatusManage.Key]);

        var (orderId, _) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 25.00m, currency: "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(context.TenantId, new CreatePaymentRequest(orderId, "Online"), CancellationToken.None);
            paymentId = res.Id;

            await svc.CancelPaymentAsync(context.TenantId, paymentId, new CancelPaymentRequest("Cancelled"), CancellationToken.None);
        }, tenantId: context.TenantId);

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();

            // 1. StartAttempt rejected with ConflictException
            await Assert.ThrowsAsync<ConflictException>(() =>
                svc.StartAttemptAsync(context.TenantId, paymentId, new StartPaymentAttemptRequest("provider-x"), CancellationToken.None));

            // 2. MarkPaid rejected with ConflictException
            await Assert.ThrowsAsync<ConflictException>(() =>
                svc.MarkPaidAsync(context.TenantId, paymentId, new MarkPaymentPaidRequest("Paid"), CancellationToken.None));
        }, tenantId: context.TenantId);
    }

    [Fact]
    public async Task OrderCancelled_ExistingPayment_IsNotSilentlyMutated()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        var (orderId, _) = await SeedOrderWithProductAsync(context.TenantId, basePrice: 50.00m, currency: "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var res = await svc.CreatePaymentForOrderAsync(context.TenantId, new CreatePaymentRequest(orderId, "Online"), CancellationToken.None);
            paymentId = res.Id;
        }, tenantId: context.TenantId);

        // Cancel order directly in ordering domain
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var order = await db.Orders.FindAsync(orderId);
            order!.Cancel(DateTimeOffset.UtcNow, reason: "Cancelled directly");
            await db.SaveChangesAsync();
        }, platformContext: true);

        // Verify Payment is still Pending in Payment module
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var payment = await svc.GetPaymentByIdAsync(context.TenantId, paymentId, CancellationToken.None);
            Assert.Equal("Pending", payment.Status);
        }, tenantId: context.TenantId);
    }
}
