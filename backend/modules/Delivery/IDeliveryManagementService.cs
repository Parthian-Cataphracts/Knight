using Delivery.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Delivery;

public interface IDeliveryManagementService
{
    Task<TenantDeliverySettings> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task<TenantDeliverySettings> UpdateSettingsAsync(Guid tenantId, bool isAcceptingDeliveryOrders, decimal? defaultMinimumOrderSubtotal, CancellationToken cancellationToken = default);
    Task<DeliveryZone?> GetZoneByIdAsync(Guid tenantId, Guid zoneId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyCollection<DeliveryZone> Items, long TotalCount)> ListZonesAsync(Guid tenantId, int page, int pageSize, DeliveryZoneStatus? status = null, CancellationToken cancellationToken = default);
    Task<DeliveryZone> CreateZoneAsync(Guid tenantId, string name, decimal fee, decimal? minimumOrderSubtotal, int displayOrder, CancellationToken cancellationToken = default);
    Task<DeliveryZone> UpdateZoneAsync(Guid tenantId, Guid zoneId, string name, decimal fee, decimal? minimumOrderSubtotal, int displayOrder, CancellationToken cancellationToken = default);
    Task<DeliveryZone> ArchiveZoneAsync(Guid tenantId, Guid zoneId, CancellationToken cancellationToken = default);
    Task<DeliveryZone> RestoreZoneAsync(Guid tenantId, Guid zoneId, CancellationToken cancellationToken = default);
}

public sealed class DeliveryManagementService : IDeliveryManagementService
{
    private readonly ITenantDeliverySettingsRepository _settingsRepository;
    private readonly IDeliveryZoneRepository _zoneRepository;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly DeliveryAuditRecorder _audit;

    public DeliveryManagementService(
        ITenantDeliverySettingsRepository settingsRepository,
        IDeliveryZoneRepository zoneRepository,
        IDateTimeProvider dateTimeProvider,
        DeliveryAuditRecorder audit)
    {
        _settingsRepository = settingsRepository;
        _zoneRepository = zoneRepository;
        _dateTimeProvider = dateTimeProvider;
        _audit = audit;
    }

    public async Task<TenantDeliverySettings> GetSettingsAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var settings = await _settingsRepository.GetByTenantIdAsync(tenantId, cancellationToken);
        if (settings is null)
        {
            return TenantDeliverySettings.Create(tenantId, _dateTimeProvider.UtcNow, isAcceptingDeliveryOrders: true, defaultMinimumOrderSubtotal: null);
        }

        return settings;
    }

    public async Task<TenantDeliverySettings> UpdateSettingsAsync(
        Guid tenantId,
        bool isAcceptingDeliveryOrders,
        decimal? defaultMinimumOrderSubtotal,
        CancellationToken cancellationToken = default)
    {
        var now = _dateTimeProvider.UtcNow;
        var settings = await _settingsRepository.GetByTenantIdAsync(tenantId, cancellationToken);

        if (settings is null)
        {
            settings = TenantDeliverySettings.Create(tenantId, now, isAcceptingDeliveryOrders, defaultMinimumOrderSubtotal);
            await _settingsRepository.AddAsync(settings, cancellationToken);
        }
        else
        {
            settings.Update(isAcceptingDeliveryOrders, defaultMinimumOrderSubtotal, now);
        }

        await _settingsRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "DeliverySettingsUpdated",
            "TenantDeliverySettings",
            tenantId,
            tenantId,
            cancellationToken,
            nonPiiMetadata: new Dictionary<string, string>
            {
                ["isAcceptingDeliveryOrders"] = isAcceptingDeliveryOrders.ToString(),
                ["defaultMinimumOrderSubtotal"] = defaultMinimumOrderSubtotal?.ToString("F2") ?? "none"
            });

        return settings;
    }

    public Task<DeliveryZone?> GetZoneByIdAsync(Guid tenantId, Guid zoneId, CancellationToken cancellationToken = default) =>
        _zoneRepository.GetByIdAsync(tenantId, zoneId, cancellationToken);

    public Task<(IReadOnlyCollection<DeliveryZone> Items, long TotalCount)> ListZonesAsync(
        Guid tenantId,
        int page,
        int pageSize,
        DeliveryZoneStatus? status = null,
        CancellationToken cancellationToken = default) =>
        _zoneRepository.ListAsync(tenantId, page, pageSize, status, cancellationToken);

    public async Task<DeliveryZone> CreateZoneAsync(
        Guid tenantId,
        string name,
        decimal fee,
        decimal? minimumOrderSubtotal,
        int displayOrder,
        CancellationToken cancellationToken = default)
    {
        var now = _dateTimeProvider.UtcNow;
        var zone = DeliveryZone.Create(
            Guid.NewGuid(),
            now,
            tenantId,
            name,
            fee,
            minimumOrderSubtotal,
            displayOrder);

        await _zoneRepository.AddAsync(zone, cancellationToken);
        await _zoneRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "DeliveryZoneCreated",
            "DeliveryZone",
            zone.Id,
            tenantId,
            cancellationToken,
            nonPiiMetadata: new Dictionary<string, string>
            {
                ["name"] = zone.Name,
                ["fee"] = zone.Fee.ToString("F2")
            });

        return zone;
    }

    public async Task<DeliveryZone> UpdateZoneAsync(
        Guid tenantId,
        Guid zoneId,
        string name,
        decimal fee,
        decimal? minimumOrderSubtotal,
        int displayOrder,
        CancellationToken cancellationToken = default)
    {
        var zone = await _zoneRepository.GetByIdAsync(tenantId, zoneId, cancellationToken);
        if (zone is null)
        {
            throw new NotFoundException("DeliveryZone", zoneId);
        }

        var now = _dateTimeProvider.UtcNow;
        zone.Update(name, fee, minimumOrderSubtotal, displayOrder, now);

        await _zoneRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "DeliveryZoneUpdated",
            "DeliveryZone",
            zone.Id,
            tenantId,
            cancellationToken,
            nonPiiMetadata: new Dictionary<string, string>
            {
                ["name"] = zone.Name,
                ["fee"] = zone.Fee.ToString("F2")
            });

        return zone;
    }

    public async Task<DeliveryZone> ArchiveZoneAsync(Guid tenantId, Guid zoneId, CancellationToken cancellationToken = default)
    {
        var zone = await _zoneRepository.GetByIdAsync(tenantId, zoneId, cancellationToken);
        if (zone is null)
        {
            throw new NotFoundException("DeliveryZone", zoneId);
        }

        var now = _dateTimeProvider.UtcNow;
        zone.Archive(now);

        await _zoneRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "DeliveryZoneArchived",
            "DeliveryZone",
            zone.Id,
            tenantId,
            cancellationToken);

        return zone;
    }

    public async Task<DeliveryZone> RestoreZoneAsync(Guid tenantId, Guid zoneId, CancellationToken cancellationToken = default)
    {
        var zone = await _zoneRepository.GetByIdAsync(tenantId, zoneId, cancellationToken);
        if (zone is null)
        {
            throw new NotFoundException("DeliveryZone", zoneId);
        }

        var now = _dateTimeProvider.UtcNow;
        zone.Restore(now);

        await _zoneRepository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "DeliveryZoneRestored",
            "DeliveryZone",
            zone.Id,
            tenantId,
            cancellationToken);

        return zone;
    }
}
