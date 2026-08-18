using Ordering.Domain;
using Knight.Application.Abstractions.Identity;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Ordering;

public sealed class OrderTests
{
    [Fact]
    public void Create_ValidInputs_InitializesCorrectly()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var orderId = Guid.NewGuid();

        var item = OrderItem.Create(
            Guid.NewGuid(),
            tenantId,
            orderId,
            Guid.NewGuid(),
            "Coffee",
            null,
            null,
            unitBasePrice: 4.50m,
            quantity: 2,
            displayOrder: 0);

        var order = Order.Create(
            orderId,
            now,
            tenantId,
            orderNumber: 1001,
            currency: "USD",
            items: [item]);

        Assert.Equal(orderId, order.Id);
        Assert.Equal(tenantId, order.TenantId);
        Assert.Equal(1001, order.OrderNumber);
        Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Equal("USD", order.Currency);
        Assert.Equal(9.00m, order.Subtotal);
        Assert.Equal(9.00m, order.Total);
        Assert.Null(order.CompletedAt);
        Assert.Null(order.CancelledAt);
        Assert.Null(order.CancellationReason);
        Assert.Single(order.Items);
        Assert.Single(order.StatusHistory);

        var initialHistory = order.StatusHistory.First();
        Assert.Null(initialHistory.FromStatus);
        Assert.Equal(OrderStatus.Pending, initialHistory.ToStatus);
        Assert.Equal(now, initialHistory.ChangedAt);
    }

    [Fact]
    public void Create_EmptyTenantId_ThrowsValidationException()
    {
        var item = OrderItem.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Tea",
            null,
            null,
            unitBasePrice: 3.00m,
            quantity: 1,
            displayOrder: 0);

        var ex = Assert.Throws<DomainException>(() =>
            Order.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.Empty, 1001, "USD", [item]));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void Create_ZeroOrNegativeOrderNumber_ThrowsValidationException()
    {
        var tenantId = Guid.NewGuid();
        var item = OrderItem.Create(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Tea",
            null,
            null,
            unitBasePrice: 3.00m,
            quantity: 1,
            displayOrder: 0);

        var ex = Assert.Throws<DomainException>(() =>
            Order.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, 0, "USD", [item]));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("US")]
    [InlineData("USDD")]
    public void Create_InvalidCurrency_ThrowsValidationException(string currency)
    {
        var tenantId = Guid.NewGuid();
        var item = OrderItem.Create(
            Guid.NewGuid(),
            tenantId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Tea",
            null,
            null,
            unitBasePrice: 3.00m,
            quantity: 1,
            displayOrder: 0);

        var ex = Assert.Throws<DomainException>(() =>
            Order.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, tenantId, 1001, currency, [item]));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void Create_EmptyItems_ThrowsValidationException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            Order.Create(Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid(), 1001, "USD", []));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void Create_CalculatesSubtotalAndTotalAcrossMultipleItems()
    {
        var tenantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();

        var item1 = OrderItem.Create(
            Guid.NewGuid(),
            tenantId,
            orderId,
            Guid.NewGuid(),
            "Burger",
            null,
            null,
            unitBasePrice: 10.00m,
            quantity: 2,
            displayOrder: 0);

        var item2 = OrderItem.Create(
            Guid.NewGuid(),
            tenantId,
            orderId,
            Guid.NewGuid(),
            "Fries",
            null,
            null,
            unitBasePrice: 3.50m,
            quantity: 1,
            displayOrder: 1);

        var order = Order.Create(
            orderId,
            DateTimeOffset.UtcNow,
            tenantId,
            1001,
            "USD",
            [item1, item2]);

        Assert.Equal(23.50m, order.Subtotal);
        Assert.Equal(23.50m, order.Total);
    }
}
