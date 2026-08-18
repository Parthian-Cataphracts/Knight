using Fulfillment.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

public sealed class TenantFulfillmentSettingsRepository : ITenantFulfillmentSettingsRepository
{
    private readonly PlatformDbContext _dbContext;

    public TenantFulfillmentSettingsRepository(PlatformDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<TenantFulfillmentSettings?> GetByTenantIdAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        _dbContext.TenantFulfillmentSettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

    public async Task AddAsync(TenantFulfillmentSettings settings, CancellationToken cancellationToken = default)
    {
        await _dbContext.TenantFulfillmentSettings.AddAsync(settings, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
