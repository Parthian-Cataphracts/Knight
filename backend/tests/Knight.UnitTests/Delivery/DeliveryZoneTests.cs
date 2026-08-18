using Delivery.Domain;
using Knight.Domain.Exceptions;

namespace Knight.UnitTests.Delivery;

public sealed class DeliveryZoneTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Create_WithValidParameters_SetsPropertiesCorrectly()
    {
        var id = Guid.NewGuid();
        var zone = DeliveryZone.Create(
            id,
            Now,
            TenantId,
            "Downtown",
            5.00m,
            20.00m,
            1);

        Assert.Equal(id, zone.Id);
        Assert.Equal(TenantId, zone.TenantId);
        Assert.Equal("Downtown", zone.Name);
        Assert.Equal(5.00m, zone.Fee);
        Assert.Equal(20.00m, zone.MinimumOrderSubtotal);
        Assert.Equal(1, zone.DisplayOrder);
        Assert.Equal(DeliveryZoneStatus.Active, zone.Status);
        Assert.Null(zone.ArchivedAt);
        Assert.Equal(Now, zone.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public void Create_WithEmptyName_ThrowsDomainException(string? name)
    {
        Assert.Throws<DomainException>(() =>
            DeliveryZone.Create(Guid.NewGuid(), Now, TenantId, name!, 5.00m, 20.00m, 1));
    }

    [Fact]
    public void Create_WithNameExceeding100Chars_ThrowsDomainException()
    {
        var longName = new string('A', 101);
        Assert.Throws<DomainException>(() =>
            DeliveryZone.Create(Guid.NewGuid(), Now, TenantId, longName, 5.00m, 20.00m, 1));
    }

    [Fact]
    public void Create_WithNegativeFee_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            DeliveryZone.Create(Guid.NewGuid(), Now, TenantId, "Downtown", -1.00m, 20.00m, 1));
    }

    [Fact]
    public void Create_WithNegativeMinimumOrderSubtotal_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            DeliveryZone.Create(Guid.NewGuid(), Now, TenantId, "Downtown", 5.00m, -5.00m, 1));
    }

    [Fact]
    public void Update_WithValidValues_UpdatesPropertiesAndSetsUpdatedAt()
    {
        var zone = DeliveryZone.Create(Guid.NewGuid(), Now, TenantId, "Downtown", 5.00m, 20.00m, 1);
        var updateTime = Now.AddMinutes(10);

        zone.Update("Midtown", 7.50m, 25.00m, 2, updateTime);

        Assert.Equal("Midtown", zone.Name);
        Assert.Equal(7.50m, zone.Fee);
        Assert.Equal(25.00m, zone.MinimumOrderSubtotal);
        Assert.Equal(2, zone.DisplayOrder);
        Assert.Equal(updateTime, zone.UpdatedAt);
    }

    [Fact]
    public void Archive_WhenActive_SetsStatusArchivedAndArchivedAt()
    {
        var zone = DeliveryZone.Create(Guid.NewGuid(), Now, TenantId, "Downtown", 5.00m, 20.00m, 1);
        var archiveTime = Now.AddHours(1);

        zone.Archive(archiveTime);

        Assert.Equal(DeliveryZoneStatus.Archived, zone.Status);
        Assert.Equal(archiveTime, zone.ArchivedAt);
        Assert.Equal(archiveTime, zone.UpdatedAt);
    }

    [Fact]
    public void Archive_WhenAlreadyArchived_ThrowsDomainException()
    {
        var zone = DeliveryZone.Create(Guid.NewGuid(), Now, TenantId, "Downtown", 5.00m, 20.00m, 1);
        var archiveTime = Now.AddHours(1);

        zone.Archive(archiveTime);

        Assert.Throws<DomainException>(() => zone.Archive(archiveTime.AddMinutes(5)));
    }

    [Fact]
    public void Restore_WhenArchived_SetsStatusActiveAndClearsArchivedAt()
    {
        var zone = DeliveryZone.Create(Guid.NewGuid(), Now, TenantId, "Downtown", 5.00m, 20.00m, 1);
        var archiveTime = Now.AddHours(1);
        zone.Archive(archiveTime);

        var restoreTime = archiveTime.AddHours(1);
        zone.Restore(restoreTime);

        Assert.Equal(DeliveryZoneStatus.Active, zone.Status);
        Assert.Null(zone.ArchivedAt);
        Assert.Equal(restoreTime, zone.UpdatedAt);
    }
}
