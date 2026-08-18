using Microsoft.EntityFrameworkCore;
using Ordering.Domain;
using Knight.Infrastructure.Persistence;

namespace Knight.Infrastructure.Adapters;

internal sealed class OrderingTenantReader : IOrderingTenantReader
{
    private readonly PlatformDbContext _context;

    public OrderingTenantReader(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<OrderingTenantSnapshot?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _context.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId, cancellationToken);

        if (tenant is null)
        {
            return null;
        }

        return new OrderingTenantSnapshot(
            tenant.Id,
            tenant.DefaultCurrency,
            tenant.Status.ToString());
    }
}
