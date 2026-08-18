using Ordering.Domain;
using Knight.Domain.Exceptions;

namespace Knight.UnitTests.Ordering;

public sealed class OrderPartySnapshotTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _orderId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void CreateFromCustomer_ValidDetails_CreatesSnapshotWithSourceCustomerId()
    {
        var customerId = Guid.NewGuid();
        var snapshot = OrderPartySnapshot.CreateFromCustomer(
            Guid.NewGuid(),
            _now,
            _tenantId,
            _orderId,
            customerId,
            "Ali Reza",
            "+15551234567",
            "ali@example.com");

        Assert.Equal(_tenantId, snapshot.TenantId);
        Assert.Equal(_orderId, snapshot.OrderId);
        Assert.Equal(customerId, snapshot.SourceCustomerId);
        Assert.Equal("Ali Reza", snapshot.DisplayName);
        Assert.Equal("+15551234567", snapshot.Phone);
        Assert.Equal("ali@example.com", snapshot.Email);
    }

    [Fact]
    public void CreateFromCustomer_EmptyCustomerId_ThrowsValidationException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderPartySnapshot.CreateFromCustomer(
                Guid.NewGuid(),
                _now,
                _tenantId,
                _orderId,
                Guid.Empty,
                "Ali Reza",
                "+15551234567",
                null));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void CreateFromGuest_ValidDetails_CreatesSnapshotWithoutSourceCustomerId()
    {
        var snapshot = OrderPartySnapshot.CreateFromGuest(
            Guid.NewGuid(),
            _now,
            _tenantId,
            _orderId,
            "Guest Buyer",
            "+989123456789",
            "guest@domain.com");

        Assert.Equal(_tenantId, snapshot.TenantId);
        Assert.Equal(_orderId, snapshot.OrderId);
        Assert.Null(snapshot.SourceCustomerId);
        Assert.Equal("Guest Buyer", snapshot.DisplayName);
        Assert.Equal("+989123456789", snapshot.Phone);
        Assert.Equal("guest@domain.com", snapshot.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateFromGuest_EmptyDisplayName_ThrowsValidationException(string name)
    {
        var ex = Assert.Throws<DomainException>(() =>
            OrderPartySnapshot.CreateFromGuest(
                Guid.NewGuid(),
                _now,
                _tenantId,
                _orderId,
                name,
                "+15551234567",
                null));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void Order_CreateWithPartySnapshot_AssignsPartyProperty()
    {
        var item = OrderItem.Create(
            Guid.NewGuid(),
            _tenantId,
            _orderId,
            Guid.NewGuid(),
            "Coffee",
            null,
            null,
            5.00m,
            2,
            0,
            []);

        var party = OrderPartySnapshot.CreateFromGuest(
            Guid.NewGuid(),
            _now,
            _tenantId,
            _orderId,
            "Guest",
            "+15551234567",
            null);

        var order = Order.Create(
            _orderId,
            _now,
            _tenantId,
            1001,
            "USD",
            [item],
            party: party);

        Assert.NotNull(order.Party);
        Assert.Equal("Guest", order.Party.DisplayName);
        Assert.Equal("+15551234567", order.Party.Phone);
    }
}
