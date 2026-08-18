using Microsoft.EntityFrameworkCore;
using Ordering.Domain;
using Payment.Domain;
using Knight.Infrastructure.Persistence;

namespace Knight.Infrastructure.Adapters;

public sealed class PaymentOrderReader : IPaymentOrderReader
{
    private readonly PlatformDbContext _context;

    public PaymentOrderReader(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<PaymentOrderSnapshot?> GetOrderSnapshotAsync(
        Guid tenantId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        // Deliberately keeps the global tenant query filter engaged: the explicit
        // TenantId predicate below is the primary scope, and the filter remains as a
        // second line of defense should a caller ever pass a tenant it does not own.
        var order = await _context.Orders
            .AsNoTracking()
            .FirstOrDefaultAsync(o => o.TenantId == tenantId && o.Id == orderId, cancellationToken);

        if (order is null)
        {
            return null;
        }

        return new PaymentOrderSnapshot(
            order.Id,
            order.TenantId,
            order.Total,
            order.Currency,
            order.Status.ToString(),
            order.Status == OrderStatus.Cancelled);
    }
}
