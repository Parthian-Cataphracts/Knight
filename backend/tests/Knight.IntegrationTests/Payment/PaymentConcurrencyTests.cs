using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Domain;
using Payment;
using Payment.Domain;
using Knight.Application.Exceptions;
using Knight.Contracts.Payment;
using Knight.IntegrationTests.Infrastructure;
using Xunit;
using PaymentEntity = global::Payment.Domain.Payment;

namespace Knight.IntegrationTests.Payment;

[Collection(PostgresCollection.Name)]
public sealed class PaymentConcurrencyTests
{
    private readonly PostgresApiFixture _fixture;

    public PaymentConcurrencyTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Guid> SeedOrderAsync(Guid tenantId, decimal basePrice = 100.00m, string currency = "USD")
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
    public async Task TwentyFiveWayConcurrentPaymentCreation_CreatesExactlyOnePayment()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        var orderId = await SeedOrderAsync(context.TenantId, 100m, "USD");

        const int concurrency = 25;
        var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            await startBarrier.Task;
            try
            {
                return await _fixture.WithScopeAsync(async (_, sp) =>
                {
                    var svc = sp.GetRequiredService<IPaymentManagementService>();
                    var res = await svc.CreatePaymentForOrderAsync(
                        context.TenantId,
                        new CreatePaymentRequest(orderId, "Online"),
                        CancellationToken.None);
                    return (Success: true, Payment: (PaymentResponse?)res, Conflict: false);
                }, tenantId: context.TenantId);
            }
            catch (ConflictException)
            {
                return (Success: false, Payment: (PaymentResponse?)null, Conflict: true);
            }
            catch (Exception)
            {
                return (Success: false, Payment: (PaymentResponse?)null, Conflict: false);
            }
        });

        var taskArray = tasks.ToArray();
        startBarrier.SetResult();
        var results = await Task.WhenAll(taskArray);

        var successCount = results.Count(r => r.Success);
        var conflictCount = results.Count(r => r.Conflict);

        Assert.Equal(1, successCount);
        Assert.Equal(concurrency - 1, conflictCount);

        // Verify in DB exactly 1 payment exists
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var count = await db.Payments.CountAsync(p => p.TenantId == context.TenantId && p.OrderId == orderId);
            Assert.Equal(1, count);
        }, platformContext: true);
    }

    [Fact]
    public async Task DuplicateAttemptNumber_ViolatesPostgresUniqueConstraint()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var payment = PaymentEntity.Create(paymentId, tenantId, Guid.NewGuid(), 100m, "USD", PaymentMethod.Online, DateTimeOffset.UtcNow);
            await db.Payments.AddAsync(payment);
            await db.SaveChangesAsync();

            var att1 = PaymentAttempt.Create(Guid.NewGuid(), tenantId, paymentId, 1, "test", DateTimeOffset.UtcNow);
            var att2 = PaymentAttempt.Create(Guid.NewGuid(), tenantId, paymentId, 1, "test", DateTimeOffset.UtcNow);

            await db.PaymentAttempts.AddRangeAsync(att1, att2);

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }, platformContext: true);
    }

    [Fact]
    public async Task ConcurrentAttemptSuccess_AtMostOneWinnerEstablishesSucceededState()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        var orderId = await SeedOrderAsync(context.TenantId, 100m, "USD");

        Guid paymentId = Guid.Empty;
        Guid attempt1Id = Guid.Empty;
        Guid attempt2Id = Guid.Empty;

        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var paymentRes = await svc.CreatePaymentForOrderAsync(context.TenantId, new CreatePaymentRequest(orderId, "Online"), CancellationToken.None);
            paymentId = paymentRes.Id;

            var att1 = await svc.StartAttemptAsync(context.TenantId, paymentId, new StartPaymentAttemptRequest("provider-1"), CancellationToken.None);
            var att2 = await svc.StartAttemptAsync(context.TenantId, paymentId, new StartPaymentAttemptRequest("provider-2"), CancellationToken.None);
            attempt1Id = att1.AttemptId;
            attempt2Id = att2.AttemptId;
        }, tenantId: context.TenantId);

        var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = Task.Run(async () =>
        {
            await startBarrier.Task;
            try
            {
                return await _fixture.WithScopeAsync(async (_, sp) =>
                {
                    var svc = sp.GetRequiredService<IPaymentManagementService>();
                    return await svc.CompleteAttemptAsync(context.TenantId, paymentId, attempt1Id, new CompletePaymentAttemptRequest("Succeeded", "ref-winner-1"), CancellationToken.None);
                }, tenantId: context.TenantId);
            }
            catch (Exception)
            {
                return (PaymentResponse?)null;
            }
        });

        var task2 = Task.Run(async () =>
        {
            await startBarrier.Task;
            try
            {
                return await _fixture.WithScopeAsync(async (_, sp) =>
                {
                    var svc = sp.GetRequiredService<IPaymentManagementService>();
                    return await svc.CompleteAttemptAsync(context.TenantId, paymentId, attempt2Id, new CompletePaymentAttemptRequest("Succeeded", "ref-winner-2"), CancellationToken.None);
                }, tenantId: context.TenantId);
            }
            catch (Exception)
            {
                return (PaymentResponse?)null;
            }
        });

        startBarrier.SetResult();
        var responses = await Task.WhenAll(task1, task2);

        // At least one returned null due to ConflictException on winning race
        var succeededResponses = responses.Where(r => r != null).ToArray();
        Assert.Single(succeededResponses);

        // Verify in DB that exactly one attempt is Succeeded and Payment is Succeeded
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var payment = await db.Payments.Include(p => p.Attempts).Include(p => p.StatusHistories).FirstAsync(p => p.Id == paymentId);
            Assert.Equal(PaymentStatus.Succeeded, payment.Status);

            var succeededAttempts = payment.Attempts.Count(a => a.Status == PaymentAttemptStatus.Succeeded);
            Assert.Equal(1, succeededAttempts);
        }, platformContext: true);
    }

    [Fact]
    public async Task TwentyFiveWayConcurrentStartAttempt_AllocatesSequentialAttemptNumbersWithoutDuplicates()
    {
        if (!_fixture.IsAvailable) return;

        var context = await _fixture.SeedPaymentTenantAsync(paymentFeatureEnabled: true);
        var orderId = await SeedOrderAsync(context.TenantId, 100m, "USD");

        Guid paymentId = Guid.Empty;
        await _fixture.WithScopeAsync(async (_, sp) =>
        {
            var svc = sp.GetRequiredService<IPaymentManagementService>();
            var paymentRes = await svc.CreatePaymentForOrderAsync(
                context.TenantId,
                new CreatePaymentRequest(orderId, "Online"),
                CancellationToken.None);
            paymentId = paymentRes.Id;
        }, tenantId: context.TenantId);

        const int concurrency = 25;
        var startBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, concurrency).Select(async i =>
        {
            await startBarrier.Task;
            try
            {
                return await _fixture.WithScopeAsync(async (_, sp) =>
                {
                    var svc = sp.GetRequiredService<IPaymentManagementService>();
                    var res = await svc.StartAttemptAsync(
                        context.TenantId,
                        paymentId,
                        new StartPaymentAttemptRequest($"provider-{i}"),
                        CancellationToken.None);
                    return (Success: true, Attempt: (StartPaymentAttemptResponse?)res);
                }, tenantId: context.TenantId);
            }
            catch (Exception)
            {
                return (Success: false, Attempt: (StartPaymentAttemptResponse?)null);
            }
        });

        startBarrier.SetResult();
        var results = await Task.WhenAll(tasks);

        var succeeded = results.Where(r => r.Success && r.Attempt != null).Select(r => r.Attempt!).ToArray();
        Assert.Equal(concurrency, succeeded.Length);

        var attemptNumbers = succeeded.Select(a => a.AttemptNumber).OrderBy(n => n).ToArray();
        var expectedNumbers = Enumerable.Range(1, concurrency).ToArray();
        Assert.Equal(expectedNumbers, attemptNumbers);

        // Verify in DB all 25 attempts exist with distinct attempt numbers
        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var attempts = await db.PaymentAttempts
                .Where(a => a.TenantId == context.TenantId && a.PaymentId == paymentId)
                .OrderBy(a => a.AttemptNumber)
                .ToListAsync();

            Assert.Equal(concurrency, attempts.Count);
            for (var i = 0; i < concurrency; i++)
            {
                Assert.Equal(i + 1, attempts[i].AttemptNumber);
            }
        }, platformContext: true);
    }

    [Fact]
    public async Task StaleDbContext_PaymentMutation_ThrowsConcurrencyConflict()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var payment = PaymentEntity.Create(paymentId, tenantId, orderId, 50m, "USD", PaymentMethod.Online, now);
            await db.Payments.AddAsync(payment);
            await db.SaveChangesAsync();
        }, platformContext: true);

        // Context A loads Payment
        // Context B loads same Payment
        await _fixture.WithScopeAsync(async (dbA, _) =>
        {
            await _fixture.WithScopeAsync(async (dbB, _) =>
            {
                var paymentA = await dbA.Payments.FirstAsync(p => p.Id == paymentId);
                var paymentB = await dbB.Payments.FirstAsync(p => p.Id == paymentId);

                paymentA.TransitionToCancelled(now.AddMinutes(1));
                await dbA.SaveChangesAsync(); // Version increments to 2

                paymentB.TransitionToCancelled(now.AddMinutes(2));
                // DbContext B still has Version = 1 in original values, saving must throw DbUpdateConcurrencyException
                await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
            }, platformContext: true);
        }, platformContext: true);
    }

    [Fact]
    public async Task SameTenant_SameOrderId_DirectPostgresDuplicate_ViolatesUniqueConstraint()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var p1 = PaymentEntity.Create(Guid.NewGuid(), tenantId, orderId, 100m, "USD", PaymentMethod.Online, now);
            var p2 = PaymentEntity.Create(Guid.NewGuid(), tenantId, orderId, 100m, "USD", PaymentMethod.Online, now);

            await db.Payments.AddAsync(p1);
            await db.SaveChangesAsync();

            await db.Payments.AddAsync(p2);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }, platformContext: true);
    }

    [Fact]
    public async Task DifferentTenant_SameOrderId_DirectPostgres_Succeeds()
    {
        if (!_fixture.IsAvailable) return;

        var tenantId1 = Guid.NewGuid();
        var tenantId2 = Guid.NewGuid();
        var sharedOrderId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _fixture.WithScopeAsync(async (db, _) =>
        {
            var p1 = PaymentEntity.Create(Guid.NewGuid(), tenantId1, sharedOrderId, 100m, "USD", PaymentMethod.Online, now);
            var p2 = PaymentEntity.Create(Guid.NewGuid(), tenantId2, sharedOrderId, 100m, "USD", PaymentMethod.Online, now);

            await db.Payments.AddRangeAsync(p1, p2);
            await db.SaveChangesAsync();

            var count = await db.Payments.CountAsync(p => p.OrderId == sharedOrderId);
            Assert.Equal(2, count);
        }, platformContext: true);
    }
}
