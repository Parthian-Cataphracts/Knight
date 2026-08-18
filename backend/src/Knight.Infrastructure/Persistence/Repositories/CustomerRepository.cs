using Customer.Domain;
using Microsoft.EntityFrameworkCore;

namespace Knight.Infrastructure.Persistence.Repositories;

internal sealed class CustomerRepository : ICustomerRepository
{
    private readonly PlatformDbContext _context;

    public CustomerRepository(PlatformDbContext context)
    {
        _context = context;
    }

    public async Task<Customer.Domain.Customer?> GetByIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        return await _context.Customers
            .FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == customerId, cancellationToken);
    }

    public async Task<(IReadOnlyCollection<Customer.Domain.Customer> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CustomerListFilter filter,
        CancellationToken cancellationToken)
    {
        var query = _context.Customers
            .AsNoTracking()
            .Where(c => c.TenantId == tenantId);

        if (filter.Status.HasValue)
        {
            query = query.Where(c => c.Status == filter.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var term = filter.SearchTerm.Trim();
            var lowerTerm = term.ToLowerInvariant();
            string? normPhone = null;
            try
            {
                var (_, np) = CustomerNormalization.NormalizePhone(term);
                normPhone = np;
            }
            catch
            {
                // Non-phone search term
            }

            query = query.Where(c =>
                c.DisplayName.ToLower().Contains(lowerTerm) ||
                (c.NormalizedEmail != null && c.NormalizedEmail.Contains(lowerTerm)) ||
                (c.NormalizedPhone != null && normPhone != null && c.NormalizedPhone.Contains(normPhone)));
        }

        var totalCount = await query.LongCountAsync(cancellationToken);

        var items = await query
            .OrderBy(c => c.DisplayName)
            .ThenByDescending(c => c.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(Customer.Domain.Customer customer, CancellationToken cancellationToken)
    {
        await _context.Customers.AddAsync(customer, cancellationToken);
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        return _context.SaveChangesAsync(cancellationToken);
    }
}
