namespace Delivery.Domain;

public interface IDeliveryZoneRepository
{
    Task<DeliveryZone?> GetByIdAsync(Guid tenantId, Guid zoneId, CancellationToken cancellationToken = default);
    Task<(IReadOnlyCollection<DeliveryZone> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, DeliveryZoneStatus? status = null, CancellationToken cancellationToken = default);
    Task AddAsync(DeliveryZone zone, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
