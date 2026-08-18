namespace Fulfillment.Domain;

public interface ITenantFulfillmentSettingsRepository
{
    Task<TenantFulfillmentSettings?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default);
    Task AddAsync(TenantFulfillmentSettings settings, CancellationToken cancellationToken = default);
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
