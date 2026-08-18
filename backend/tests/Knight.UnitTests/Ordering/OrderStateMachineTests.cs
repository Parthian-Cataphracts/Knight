using Ordering.Domain;
using Knight.Application.Abstractions.Identity;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Ordering;

public sealed class OrderStateMachineTests
{
    private static Order CreatePendingOrder()
    {
        var tenantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var item = OrderItem.Create(
            Guid.NewGuid(),
            tenantId,
            orderId,
            Guid.NewGuid(),
            "Coffee",
            null,
            null,
            unitBasePrice: 5.00m,
            quantity: 1,
            displayOrder: 0);

        return Order.Create(
            orderId,
            DateTimeOffset.UtcNow,
            tenantId,
            1001,
            "USD",
            [item]);
    }

    [Fact]
    public void FullHappyPath_TransitionsThroughAllStatuses_ToCompleted()
    {
        var order = CreatePendingOrder();
        var staffId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Single(order.StatusHistory);

        // Pending -> Confirmed
        order.Confirm(now.AddMinutes(1), staffId, PrincipalType.TenantUser);
        Assert.Equal(OrderStatus.Confirmed, order.Status);
        Assert.Equal(2, order.StatusHistory.Count);

        // Confirmed -> Preparing
        order.Prepare(now.AddMinutes(2), staffId, PrincipalType.TenantUser);
        Assert.Equal(OrderStatus.Preparing, order.Status);
        Assert.Equal(3, order.StatusHistory.Count);

        // Preparing -> Ready
        order.Ready(now.AddMinutes(3), staffId, PrincipalType.TenantUser);
        Assert.Equal(OrderStatus.Ready, order.Status);
        Assert.Equal(4, order.StatusHistory.Count);

        // Ready -> Completed
        order.Complete(now.AddMinutes(4), staffId, PrincipalType.TenantUser);
        Assert.Equal(OrderStatus.Completed, order.Status);
        Assert.NotNull(order.CompletedAt);
        Assert.Equal(now.AddMinutes(4), order.CompletedAt);
        Assert.Equal(5, order.StatusHistory.Count);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Preparing)]
    [InlineData(OrderStatus.Ready)]
    public void Cancel_FromNonTerminalStatuses_Succeeds(OrderStatus initialStatus)
    {
        var order = CreatePendingOrder();
        var now = DateTimeOffset.UtcNow;
        var staffId = Guid.NewGuid();

        if (initialStatus == OrderStatus.Confirmed)
        {
            order.Confirm(now.AddMinutes(1), staffId, PrincipalType.TenantUser);
        }
        else if (initialStatus == OrderStatus.Preparing)
        {
            order.Confirm(now.AddMinutes(1), staffId, PrincipalType.TenantUser);
            order.Prepare(now.AddMinutes(2), staffId, PrincipalType.TenantUser);
        }
        else if (initialStatus == OrderStatus.Ready)
        {
            order.Confirm(now.AddMinutes(1), staffId, PrincipalType.TenantUser);
            order.Prepare(now.AddMinutes(2), staffId, PrincipalType.TenantUser);
            order.Ready(now.AddMinutes(3), staffId, PrincipalType.TenantUser);
        }

        var cancelTime = now.AddMinutes(10);
        order.Cancel(cancelTime, staffId, PrincipalType.TenantUser, "Customer request");

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.NotNull(order.CancelledAt);
        Assert.Equal(cancelTime, order.CancelledAt);
        Assert.Equal("Customer request", order.CancellationReason);

        var latestHistory = order.StatusHistory.Last();
        Assert.Equal(initialStatus, latestHistory.FromStatus);
        Assert.Equal(OrderStatus.Cancelled, latestHistory.ToStatus);
        Assert.Equal("Customer request", latestHistory.Reason);
    }

    [Fact]
    public void Completed_IsTerminal_CannotTransitionToAnyStatus()
    {
        var order = CreatePendingOrder();
        var now = DateTimeOffset.UtcNow;
        var staffId = Guid.NewGuid();

        order.Confirm(now.AddMinutes(1), staffId, PrincipalType.TenantUser);
        order.Prepare(now.AddMinutes(2), staffId, PrincipalType.TenantUser);
        order.Ready(now.AddMinutes(3), staffId, PrincipalType.TenantUser);
        order.Complete(now.AddMinutes(4), staffId, PrincipalType.TenantUser);

        Assert.Equal(OrderStatus.Completed, order.Status);

        Assert.Throws<DomainException>(() => order.Confirm(now, staffId, PrincipalType.TenantUser));
        Assert.Throws<DomainException>(() => order.Prepare(now, staffId, PrincipalType.TenantUser));
        Assert.Throws<DomainException>(() => order.Ready(now, staffId, PrincipalType.TenantUser));
        Assert.Throws<DomainException>(() => order.Complete(now, staffId, PrincipalType.TenantUser));
        Assert.Throws<DomainException>(() => order.Cancel(now, staffId, PrincipalType.TenantUser, "Too late"));
    }

    [Fact]
    public void Cancelled_IsTerminal_CannotTransitionToAnyStatus()
    {
        var order = CreatePendingOrder();
        var now = DateTimeOffset.UtcNow;
        var staffId = Guid.NewGuid();

        order.Cancel(now.AddMinutes(1), staffId, PrincipalType.TenantUser, "Customer changed mind");
        Assert.Equal(OrderStatus.Cancelled, order.Status);

        Assert.Throws<DomainException>(() => order.Confirm(now, staffId, PrincipalType.TenantUser));
        Assert.Throws<DomainException>(() => order.Prepare(now, staffId, PrincipalType.TenantUser));
        Assert.Throws<DomainException>(() => order.Ready(now, staffId, PrincipalType.TenantUser));
        Assert.Throws<DomainException>(() => order.Complete(now, staffId, PrincipalType.TenantUser));
        Assert.Throws<DomainException>(() => order.Cancel(now, staffId, PrincipalType.TenantUser, "Again"));
    }

    [Theory]
    [InlineData(OrderStatus.Preparing)]
    [InlineData(OrderStatus.Ready)]
    [InlineData(OrderStatus.Completed)]
    public void Pending_IllegalDirectJumps_ThrowConflictException(OrderStatus invalidTarget)
    {
        var order = CreatePendingOrder();
        var ex = Assert.Throws<DomainException>(() =>
            order.TransitionTo(invalidTarget, DateTimeOffset.UtcNow));

        Assert.Equal(DomainErrorCategory.Conflict, ex.Category);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Ready)]
    [InlineData(OrderStatus.Completed)]
    public void Confirmed_IllegalDirectJumps_ThrowConflictException(OrderStatus invalidTarget)
    {
        var order = CreatePendingOrder();
        order.Confirm(DateTimeOffset.UtcNow);

        var ex = Assert.Throws<DomainException>(() =>
            order.TransitionTo(invalidTarget, DateTimeOffset.UtcNow));

        Assert.Equal(DomainErrorCategory.Conflict, ex.Category);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Completed)]
    public void Preparing_IllegalDirectJumps_ThrowConflictException(OrderStatus invalidTarget)
    {
        var order = CreatePendingOrder();
        order.Confirm(DateTimeOffset.UtcNow);
        order.Prepare(DateTimeOffset.UtcNow);

        var ex = Assert.Throws<DomainException>(() =>
            order.TransitionTo(invalidTarget, DateTimeOffset.UtcNow));

        Assert.Equal(DomainErrorCategory.Conflict, ex.Category);
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Preparing)]
    public void Ready_IllegalDirectJumps_ThrowConflictException(OrderStatus invalidTarget)
    {
        var order = CreatePendingOrder();
        order.Confirm(DateTimeOffset.UtcNow);
        order.Prepare(DateTimeOffset.UtcNow);
        order.Ready(DateTimeOffset.UtcNow);

        var ex = Assert.Throws<DomainException>(() =>
            order.TransitionTo(invalidTarget, DateTimeOffset.UtcNow));

        Assert.Equal(DomainErrorCategory.Conflict, ex.Category);
    }
}
