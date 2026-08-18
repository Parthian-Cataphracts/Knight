using Ordering.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Ordering;

public sealed class OrderItemModifierTests
{
    [Fact]
    public void Create_OrderItemWithModifiers_CalculatesTotalsCorrectly()
    {
        var tenantId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var orderItemId = Guid.NewGuid();

        var mod1 = OrderItemModifier.Create(
            Guid.NewGuid(),
            tenantId,
            orderItemId,
            Guid.NewGuid(),
            "Milk Options",
            Guid.NewGuid(),
            "Oat Milk",
            unitPriceDelta: 0.80m,
            displayOrder: 0);

        var mod2 = OrderItemModifier.Create(
            Guid.NewGuid(),
            tenantId,
            orderItemId,
            Guid.NewGuid(),
            "Syrup",
            Guid.NewGuid(),
            "Vanilla",
            unitPriceDelta: 0.50m,
            displayOrder: 1);

        var item = OrderItem.Create(
            orderItemId,
            tenantId,
            orderId,
            Guid.NewGuid(),
            "Latte",
            Guid.NewGuid(),
            "Large",
            unitBasePrice: 4.50m,
            quantity: 3,
            displayOrder: 0,
            modifiers: [mod1, mod2]);

        Assert.Equal(4.50m, item.UnitBasePrice);
        Assert.Equal(1.30m, item.UnitModifierTotal);
        Assert.Equal(5.80m, item.UnitPrice);
        Assert.Equal(17.40m, item.LineTotal);
        Assert.Equal(2, item.Modifiers.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Create_OrderItem_ZeroOrNegativeQuantity_ThrowsValidationException(int quantity)
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderItem.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Latte",
                null,
                null,
                unitBasePrice: 5.00m,
                quantity: quantity,
                displayOrder: 0));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void Create_OrderItem_NegativeBasePrice_ThrowsValidationException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderItem.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Latte",
                null,
                null,
                unitBasePrice: -1.00m,
                quantity: 1,
                displayOrder: 0));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void Create_OrderItemModifier_NegativePriceDelta_ThrowsValidationException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderItemModifier.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Add-ons",
                Guid.NewGuid(),
                "Extra Shot",
                unitPriceDelta: -0.50m,
                displayOrder: 0));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_OrderItemModifier_BlankName_ThrowsValidationException(string name)
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderItemModifier.Create(
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                Guid.NewGuid(),
                "Add-ons",
                Guid.NewGuid(),
                name,
                unitPriceDelta: 1.00m,
                displayOrder: 0));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }
}
