using Delivery.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

public sealed class DeliveryZoneRepository : IDeliveryZoneRepository
{
    private readonly PlatformDbContext _dbContext;

    public DeliveryZoneRepository(PlatformDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<DeliveryZone?> GetByIdAsync(Guid tenantId, Guid zoneId, CancellationToken cancellationToken = default) =>
        _dbContext.DeliveryZones
            .FirstOrDefaultAsync(z => z.TenantId == tenantId && z.Id == zoneId, cancellationToken);

    public async Task<(IReadOnlyCollection<DeliveryZone> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        DeliveryZoneStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var safePage = page < 1 ? 1 : page;
        var safePageSize = pageSize switch
        {
            < 1 => 20,
            > 100 => 100,
            _ => pageSize
        };

        var query = _dbContext.DeliveryZones
            .Where(z => z.TenantId == tenantId);

        if (status.HasValue)
        {
            query = query.Where(z => z.Status == status.Value);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderBy(z => z.DisplayOrder)
            .ThenBy(z => z.Name)
            .Skip((safePage - 1) * safePageSize)
            .Take(safePageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(DeliveryZone zone, CancellationToken cancellationToken = default)
    {
        await _dbContext.DeliveryZones.AddAsync(zone, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken = default) =>
        _dbContext.SaveChangesAsync(cancellationToken);
}
