using Delivery.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

public sealed class TenantDeliverySettingsRepository : ITenantDeliverySettingsRepository
{
    private readonly PlatformDbContext _dbContext;

    public TenantDeliverySettingsRepository(PlatformDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<TenantDeliverySettings?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        _dbContext.TenantDeliverySettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

    public async Task AddAsync(TenantDeliverySettings settings, CancellationToken cancellationToken = default)
    {
        await _dbContext.TenantDeliverySettings.AddAsync(settings, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
