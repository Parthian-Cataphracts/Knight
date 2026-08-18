using Ordering.Domain;
using Knight.Application.Abstractions.Identity;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Ordering;

public sealed class OrderStatusHistoryTests
{
    [Fact]
    public void Create_ValidHistoryRecord_StoresFieldsCorrectly()
    {
        var id = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var history = OrderStatusHistory.Create(
            id,
            now,
            tenantId,
            orderId,
            fromStatus: OrderStatus.Pending,
            toStatus: OrderStatus.Confirmed,
            changedByUserId: userId,
            changedByPrincipalType: PrincipalType.TenantUser,
            reason: "Staff confirmed");

        Assert.Equal(id, history.Id);
        Assert.Equal(tenantId, history.TenantId);
        Assert.Equal(orderId, history.OrderId);
        Assert.Equal(OrderStatus.Pending, history.FromStatus);
        Assert.Equal(OrderStatus.Confirmed, history.ToStatus);
        Assert.Equal(now, history.ChangedAt);
        Assert.Equal(userId, history.ChangedByUserId);
        Assert.Equal(PrincipalType.TenantUser, history.ChangedByPrincipalType);
        Assert.Equal("Staff confirmed", history.Reason);
    }

    [Fact]
    public void Create_EmptyTenantId_ThrowsValidationException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderStatusHistory.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Guid.Empty,
                Guid.NewGuid(),
                null,
                OrderStatus.Pending));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void Create_EmptyOrderId_ThrowsValidationException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderStatusHistory.Create(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Guid.NewGuid(),
                Guid.Empty,
                null,
                OrderStatus.Pending));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }
}
