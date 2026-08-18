using Microsoft.EntityFrameworkCore;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class OrderRepository : IOrderRepository
{
    private readonly PlatformDbContext _context;

    public OrderRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Order?> GetByIdAsync(Guid tenantId, Guid orderId, CancellationToken cancellationToken)
    {
        return await _context.Orders
            .Include(o => o.Party)
            .Include(o => o.Fulfillment)
            .Include(o => o.Promotion)
            .Include(o => o.Items)
                .ThenInclude(i => i.Modifiers)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken);
    }

    public async Task<Order?> GetByOrderNumberAsync(Guid tenantId, long orderNumber, CancellationToken cancellationToken)
    {
        return await _context.Orders
            .Include(o => o.Party)
            .Include(o => o.Fulfillment)
            .Include(o => o.Promotion)
            .Include(o => o.Items)
                .ThenInclude(i => i.Modifiers)
            .Include(o => o.StatusHistory)
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.OrderNumber == orderNumber, cancellationToken);
    }

    public async Task<(IReadOnlyCollection<Order> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        OrderListFilter filter,
        CancellationToken cancellationToken)
    {
        var query = _context.Orders
            .AsNoTracking()
            .Where(o => o.TenantId == tenantId);

        if (filter.Status.HasValue)
        {
            query = query.Where(o => o.Status == filter.Status.Value);
        }

        if (filter.OrderNumber.HasValue)
        {
            query = query.Where(o => o.OrderNumber == filter.OrderNumber.Value);
        }

        if (filter.CreatedFrom.HasValue)
        {
            query = query.Where(o => o.CreatedAt >= filter.CreatedFrom.Value);
        }

        if (filter.CreatedTo.HasValue)
        {
            query = query.Where(o => o.CreatedAt <= filter.CreatedTo.Value);
        }

        var totalCount = await query.LongCountAsync(cancellationToken);

        var items = await query
            .Include(o => o.Party)
            .Include(o => o.Fulfillment)
            .Include(o => o.Promotion)
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.OrderNumber)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        await _context.Orders.AddAsync(order, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }
}
