using Ordering.Domain;
using Knight.Domain.Exceptions;

namespace Knight.UnitTests.Ordering;

public sealed class OrderFulfillmentSnapshotTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void CreatePickup_SetsMethodPickupAndZeroFee()
    {
        var snapshot = OrderFulfillmentSnapshot.CreatePickup(
            Guid.NewGuid(),
            Now,
            TenantId,
            OrderId);

        Assert.Equal(TenantId, snapshot.TenantId);
        Assert.Equal(OrderId, snapshot.OrderId);
        Assert.Equal(OrderFulfillmentMethod.Pickup, snapshot.Method);
        Assert.Equal(0m, snapshot.FulfillmentFee);
        Assert.Null(snapshot.DeliveryZoneId);
        Assert.Null(snapshot.DeliveryZoneName);
        Assert.Null(snapshot.AddressLine1);
        Assert.Null(snapshot.City);
        Assert.Null(snapshot.Latitude);
        Assert.Null(snapshot.Longitude);
    }

    [Fact]
    public void CreateDelivery_WithValidParameters_SetsAllFields()
    {
        var zoneId = Guid.NewGuid();
        var snapshot = OrderFulfillmentSnapshot.CreateDelivery(
            Guid.NewGuid(),
            Now,
            TenantId,
            OrderId,
            fee: 6.50m,
            deliveryZoneId: zoneId,
            deliveryZoneName: "Downtown District",
            addressLine1: "123 Main St",
            addressLine2: "Apt 4B",
            city: "Metropolis",
            postalCode: "12345",
            latitude: 40.7128,
            longitude: -74.0060);

        Assert.Equal(OrderFulfillmentMethod.Delivery, snapshot.Method);
        Assert.Equal(6.50m, snapshot.FulfillmentFee);
        Assert.Equal(zoneId, snapshot.DeliveryZoneId);
        Assert.Equal("Downtown District", snapshot.DeliveryZoneName);
        Assert.Equal("123 Main St", snapshot.AddressLine1);
        Assert.Equal("Apt 4B", snapshot.AddressLine2);
        Assert.Equal("Metropolis", snapshot.City);
        Assert.Equal("12345", snapshot.PostalCode);
        Assert.Equal(40.7128, snapshot.Latitude);
        Assert.Equal(-74.0060, snapshot.Longitude);
    }

    [Fact]
    public void CreateDelivery_WithNegativeFee_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            OrderFulfillmentSnapshot.CreateDelivery(
                Guid.NewGuid(),
                Now,
                TenantId,
                OrderId,
                fee: -1.00m,
                deliveryZoneId: Guid.NewGuid(),
                deliveryZoneName: "Downtown",
                addressLine1: "123 Main St",
                addressLine2: null,
                city: "Metropolis",
                postalCode: null,
                latitude: null,
                longitude: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void CreateDelivery_WithEmptyAddressLine1_ThrowsDomainException(string? address1)
    {
        Assert.Throws<DomainException>(() =>
            OrderFulfillmentSnapshot.CreateDelivery(
                Guid.NewGuid(),
                Now,
                TenantId,
                OrderId,
                fee: 5.00m,
                deliveryZoneId: Guid.NewGuid(),
                deliveryZoneName: "Downtown",
                addressLine1: address1!,
                addressLine2: null,
                city: "Metropolis",
                postalCode: null,
                latitude: null,
                longitude: null));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void CreateDelivery_WithEmptyCity_ThrowsDomainException(string? city)
    {
        Assert.Throws<DomainException>(() =>
            OrderFulfillmentSnapshot.CreateDelivery(
                Guid.NewGuid(),
                Now,
                TenantId,
                OrderId,
                fee: 5.00m,
                deliveryZoneId: Guid.NewGuid(),
                deliveryZoneName: "Downtown",
                addressLine1: "123 Main St",
                addressLine2: null,
                city: city!,
                postalCode: null,
                latitude: null,
                longitude: null));
    }

    [Fact]
    public void CreateDelivery_WithOnlyLatitude_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            OrderFulfillmentSnapshot.CreateDelivery(
                Guid.NewGuid(),
                Now,
                TenantId,
                OrderId,
                fee: 5.00m,
                deliveryZoneId: Guid.NewGuid(),
                deliveryZoneName: "Downtown",
                addressLine1: "123 Main St",
                addressLine2: null,
                city: "Metropolis",
                postalCode: null,
                latitude: 40.0,
                longitude: null));
    }

    [Fact]
    public void CreateDelivery_WithLatitudeOutOfRange_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            OrderFulfillmentSnapshot.CreateDelivery(
                Guid.NewGuid(),
                Now,
                TenantId,
                OrderId,
                fee: 5.00m,
                deliveryZoneId: Guid.NewGuid(),
                deliveryZoneName: "Downtown",
                addressLine1: "123 Main St",
                addressLine2: null,
                city: "Metropolis",
                postalCode: null,
                latitude: 95.0,
                longitude: 10.0));
    }

    [Fact]
    public void CreateDelivery_WithLongitudeOutOfRange_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            OrderFulfillmentSnapshot.CreateDelivery(
                Guid.NewGuid(),
                Now,
                TenantId,
                OrderId,
                fee: 5.00m,
                deliveryZoneId: Guid.NewGuid(),
                deliveryZoneName: "Downtown",
                addressLine1: "123 Main St",
                addressLine2: null,
                city: "Metropolis",
                postalCode: null,
                latitude: 45.0,
                longitude: 185.0));
    }
}
