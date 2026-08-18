using Delivery.Domain;
using Knight.Application.Exceptions;

namespace Delivery;

public sealed record DeliveryQuoteResult(
    Guid ZoneId,
    string ZoneName,
    decimal Fee,
    decimal? EffectiveMinimumOrderSubtotal,
    bool IsEligible,
    string? IneligibilityReason = null);

public interface IDeliveryQuoteService
{
    Task<DeliveryQuoteResult> CalculateQuoteAsync(
        Guid tenantId,
        Guid deliveryZoneId,
        decimal orderSubtotal,
        CancellationToken cancellationToken = default);
}

public sealed class DeliveryQuoteService : IDeliveryQuoteService
{
    private readonly IDeliveryZoneRepository _zoneRepository;
    private readonly ITenantDeliverySettingsRepository _settingsRepository;

    public DeliveryQuoteService(
        IDeliveryZoneRepository zoneRepository,
        ITenantDeliverySettingsRepository settingsRepository)
    {
        _zoneRepository = zoneRepository;
        _settingsRepository = settingsRepository;
    }

    public async Task<DeliveryQuoteResult> CalculateQuoteAsync(
        Guid tenantId,
        Guid deliveryZoneId,
        decimal orderSubtotal,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetByTenantIdAsync(tenantId, cancellationToken);
        if (settings is not null && !settings.IsAcceptingDeliveryOrders)
        {
            return new DeliveryQuoteResult(
                deliveryZoneId,
                string.Empty,
                0m,
                null,
                IsEligible: false,
                IneligibilityReason: "Delivery orders are currently not being accepted.");
        }

        var zone = await _zoneRepository.GetByIdAsync(tenantId, deliveryZoneId, cancellationToken);
        if (zone is null)
        {
            return new DeliveryQuoteResult(
                deliveryZoneId,
                string.Empty,
                0m,
                null,
                IsEligible: false,
                IneligibilityReason: $"Delivery zone '{deliveryZoneId}' was not found for this tenant.");
        }

        if (zone.Status != DeliveryZoneStatus.Active)
        {
            return new DeliveryQuoteResult(
                zone.Id,
                zone.Name,
                zone.Fee,
                zone.MinimumOrderSubtotal,
                IsEligible: false,
                IneligibilityReason: $"Delivery zone '{zone.Name}' is not active.");
        }

        var effectiveMinimum = zone.MinimumOrderSubtotal ?? settings?.DefaultMinimumOrderSubtotal;

        if (effectiveMinimum.HasValue && orderSubtotal < effectiveMinimum.Value)
        {
            return new DeliveryQuoteResult(
                zone.Id,
                zone.Name,
                zone.Fee,
                effectiveMinimum,
                IsEligible: false,
                IneligibilityReason: $"Order subtotal {orderSubtotal:F2} is below the minimum required delivery subtotal of {effectiveMinimum.Value:F2}.");
        }

        return new DeliveryQuoteResult(
            zone.Id,
            zone.Name,
            zone.Fee,
            effectiveMinimum,
            IsEligible: true);
    }
}
