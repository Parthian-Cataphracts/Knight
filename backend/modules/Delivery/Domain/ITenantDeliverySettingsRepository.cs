namespace Delivery.Domain;

public interface ITenantDeliverySettingsRepository
{
    Task<TenantDeliverySettings?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task AddAsync(TenantDeliverySettings settings, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
