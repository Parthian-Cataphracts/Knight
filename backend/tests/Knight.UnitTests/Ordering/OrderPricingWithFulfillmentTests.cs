using Ordering.Domain;
using Knight.Application.Abstractions.Identity;

namespace Knight.UnitTests.Ordering;

public sealed class OrderPricingWithFulfillmentTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void OrderCreation_WithoutFulfillment_TotalEqualsSubtotal()
    {
        var orderId = Guid.NewGuid();
        var item = OrderItem.Create(
            Guid.NewGuid(),
            TenantId,
            orderId,
            Guid.NewGuid(),
            "Espresso",
            Guid.NewGuid(),
            "Single",
            3.50m,
            2,
            1);

        var order = Order.Create(
            orderId,
            Now,
            TenantId,
            orderNumber: 1001,
            currency: "USD",
            items: [item],
            actorUserId: Guid.NewGuid(),
            actorPrincipalType: PrincipalType.TenantUser,
            party: null,
            fulfillment: null);

        Assert.Equal(7.00m, order.Subtotal);
        Assert.Equal(0m, order.FulfillmentFee);
        Assert.Equal(7.00m, order.Total);
    }

    [Fact]
    public void OrderCreation_WithDeliveryFee_TotalEqualsSubtotalPlusFulfillmentFee()
    {
        var orderId = Guid.NewGuid();
        var item = OrderItem.Create(
            Guid.NewGuid(),
            TenantId,
            orderId,
            Guid.NewGuid(),
            "Burger",
            Guid.NewGuid(),
            "Double",
            12.00m,
            2,
            1);

        var fulfillment = OrderFulfillmentSnapshot.CreateDelivery(
            Guid.NewGuid(),
            Now,
            TenantId,
            orderId,
            fee: 4.50m,
            deliveryZoneId: Guid.NewGuid(),
            deliveryZoneName: "Downtown",
            addressLine1: "123 Main St",
            addressLine2: null,
            city: "Metropolis",
            postalCode: null,
            latitude: null,
            longitude: null);

        var order = Order.Create(
            orderId,
            Now,
            TenantId,
            orderNumber: 1002,
            currency: "USD",
            items: [item],
            actorUserId: Guid.NewGuid(),
            actorPrincipalType: PrincipalType.TenantUser,
            party: null,
            fulfillment: fulfillment);

        Assert.Equal(24.00m, order.Subtotal);
        Assert.Equal(4.50m, order.FulfillmentFee);
        Assert.Equal(28.50m, order.Total);
    }
}
