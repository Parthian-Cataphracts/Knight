using Customer.Domain;
using Microsoft.EntityFrameworkCore;
using Ordering.Domain;
using Knight.Infrastructure.Persistence;

namespace Knight.Infrastructure.Adapters;

internal sealed class CustomerOrderingReader : ICustomerOrderingReader
{
    private readonly PlatformDbContext _context;

    public CustomerOrderingReader(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<CustomerOrderingSnapshot?> GetCustomerSnapshotAsync(
        Guid tenantId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var customer = await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == customerId, cancellationToken);

        if (customer is null)
        {
            return null;
        }

        return new CustomerOrderingSnapshot(
            customer.Id,
            customer.DisplayName,
            customer.Phone,
            customer.Email,
            customer.Status == CustomerStatus.Active);
    }
}
