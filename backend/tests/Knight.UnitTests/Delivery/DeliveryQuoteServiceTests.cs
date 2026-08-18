using Delivery;
using Delivery.Domain;
using NSubstitute;

namespace Knight.UnitTests.Delivery;

public sealed class DeliveryQuoteServiceTests
{
    private static readonly Guid TenantId = Guid.NewGuid();
    private static readonly Guid ZoneId = Guid.NewGuid();

    private readonly ITenantDeliverySettingsRepository _settingsRepo = Substitute.For<ITenantDeliverySettingsRepository>();
    private readonly IDeliveryZoneRepository _zoneRepo = Substitute.For<IDeliveryZoneRepository>();
    private readonly DeliveryQuoteService _service;

    public DeliveryQuoteServiceTests()
    {
        _service = new DeliveryQuoteService(_zoneRepo, _settingsRepo);
    }

    [Fact]
    public async Task CalculateQuoteAsync_WhenNotAcceptingDelivery_ReturnsIneligible()
    {
        var settings = TenantDeliverySettings.Create(TenantId, DateTimeOffset.UtcNow, isAcceptingDeliveryOrders: false, defaultMinimumOrderSubtotal: null);
        _settingsRepo.GetByTenantIdAsync(TenantId, Arg.Any<CancellationToken>()).Returns(settings);

        var result = await _service.CalculateQuoteAsync(TenantId, ZoneId, 50.00m);

        Assert.False(result.IsEligible);
        Assert.Contains("not being accepted", result.IneligibilityReason);
    }

    [Fact]
    public async Task CalculateQuoteAsync_WhenZoneNotFound_ReturnsIneligible()
    {
        _zoneRepo.GetByIdAsync(TenantId, ZoneId, Arg.Any<CancellationToken>())
            .Returns((DeliveryZone?)null);

        var result = await _service.CalculateQuoteAsync(TenantId, ZoneId, 50.00m);

        Assert.False(result.IsEligible);
        Assert.Contains("not found", result.IneligibilityReason);
    }

    [Fact]
    public async Task CalculateQuoteAsync_WhenZoneIsArchived_ReturnsIneligible()
    {
        var zone = DeliveryZone.Create(ZoneId, DateTimeOffset.UtcNow, TenantId, "Downtown", 5.00m, 20.00m, 1);
        zone.Archive(DateTimeOffset.UtcNow);
        _zoneRepo.GetByIdAsync(TenantId, ZoneId, Arg.Any<CancellationToken>()).Returns(zone);

        var result = await _service.CalculateQuoteAsync(TenantId, ZoneId, 50.00m);

        Assert.False(result.IsEligible);
        Assert.Contains("not active", result.IneligibilityReason);
    }

    [Fact]
    public async Task CalculateQuoteAsync_WhenSubtotalBelowZoneMinimum_ReturnsIneligible()
    {
        var zone = DeliveryZone.Create(ZoneId, DateTimeOffset.UtcNow, TenantId, "Downtown", 5.00m, 30.00m, 1);
        _zoneRepo.GetByIdAsync(TenantId, ZoneId, Arg.Any<CancellationToken>()).Returns(zone);

        var result = await _service.CalculateQuoteAsync(TenantId, ZoneId, 25.00m);

        Assert.False(result.IsEligible);
        Assert.Contains("below the minimum", result.IneligibilityReason);
    }

    [Fact]
    public async Task CalculateQuoteAsync_WhenZoneMinimumNull_FallsBackToTenantDefaultMinimum()
    {
        var settings = TenantDeliverySettings.Create(TenantId, DateTimeOffset.UtcNow, isAcceptingDeliveryOrders: true, defaultMinimumOrderSubtotal: 35.00m);
        _settingsRepo.GetByTenantIdAsync(TenantId, Arg.Any<CancellationToken>()).Returns(settings);

        var zone = DeliveryZone.Create(ZoneId, DateTimeOffset.UtcNow, TenantId, "Downtown", 5.00m, null, 1);
        _zoneRepo.GetByIdAsync(TenantId, ZoneId, Arg.Any<CancellationToken>()).Returns(zone);

        var result = await _service.CalculateQuoteAsync(TenantId, ZoneId, 30.00m);

        Assert.False(result.IsEligible);
        Assert.Contains("below the minimum", result.IneligibilityReason);
    }

    [Fact]
    public async Task CalculateQuoteAsync_WhenZoneMinimumSpecified_OverridesTenantDefaultMinimum()
    {
        var settings = TenantDeliverySettings.Create(TenantId, DateTimeOffset.UtcNow, isAcceptingDeliveryOrders: true, defaultMinimumOrderSubtotal: 50.00m);
        _settingsRepo.GetByTenantIdAsync(TenantId, Arg.Any<CancellationToken>()).Returns(settings);

        // Zone has lower minimum of 20.00m, which should take precedence
        var zone = DeliveryZone.Create(ZoneId, DateTimeOffset.UtcNow, TenantId, "Downtown", 5.00m, 20.00m, 1);
        _zoneRepo.GetByIdAsync(TenantId, ZoneId, Arg.Any<CancellationToken>()).Returns(zone);

        var quote = await _service.CalculateQuoteAsync(TenantId, ZoneId, 25.00m);

        Assert.True(quote.IsEligible);
        Assert.Equal(5.00m, quote.Fee);
        Assert.Equal(ZoneId, quote.ZoneId);
        Assert.Equal("Downtown", quote.ZoneName);
        Assert.Equal(20.00m, quote.EffectiveMinimumOrderSubtotal);
    }

    [Fact]
    public async Task CalculateQuoteAsync_WhenValid_ReturnsComputedQuote()
    {
        var zone = DeliveryZone.Create(ZoneId, DateTimeOffset.UtcNow, TenantId, "North End", 8.50m, 15.00m, 1);
        _zoneRepo.GetByIdAsync(TenantId, ZoneId, Arg.Any<CancellationToken>()).Returns(zone);

        var quote = await _service.CalculateQuoteAsync(TenantId, ZoneId, 20.00m);

        Assert.True(quote.IsEligible);
        Assert.Equal(8.50m, quote.Fee);
        Assert.Equal(ZoneId, quote.ZoneId);
        Assert.Equal("North End", quote.ZoneName);
        Assert.Equal(15.00m, quote.EffectiveMinimumOrderSubtotal);
    }
}
