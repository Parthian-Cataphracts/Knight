using Customers.Domain;

// The legacy store-side module is also named Customer, so the control-plane
// aggregate is aliased rather than resolved by bare name.
using ControlPlaneCustomer = Customers.Domain.Customer;
using Knight.Application.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Knight.Infrastructure.ControlPlane.Repositories;

/// <summary>
/// Control-plane customers. Every query goes through the context's isolation
/// filter, so a customer-scoped caller reading "all customers" reads exactly
/// one: their own.
/// </summary>
internal sealed class ControlPlaneCustomerRepository : ICustomerRepository
{
    private const string PostgresUniqueViolationSqlState = "23505";

    private readonly ControlPlaneDbContext _context;

    public ControlPlaneCustomerRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public Task<ControlPlaneCustomer?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        _context.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    public Task<bool> ExistsWithContactEmailAsync(string normalizedEmail, Guid? excludingId, CancellationToken cancellationToken) =>
        _context.Customers.AnyAsync(
            c => c.ContactEmail == normalizedEmail && (excludingId == null || c.Id != excludingId),
            cancellationToken);

    public async Task<(IReadOnlyCollection<ControlPlaneCustomer> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        CustomerStatus? status,
        string? search,
        CancellationToken cancellationToken)
    {
        var query = _context.Customers.AsQueryable();

        if (status is not null)
        {
            query = query.Where(c => c.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            query = query.Where(c =>
                EF.Functions.ILike(c.Name, term) ||
                EF.Functions.ILike(c.ContactEmail, term) ||
                (c.LegalName != null && EF.Functions.ILike(c.LegalName, term)));
        }

        var ordered = query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id);

        var totalCount = await ordered.LongCountAsync(cancellationToken);
        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task AddAsync(ControlPlaneCustomer customer, CancellationToken cancellationToken) =>
        await _context.Customers.AddAsync(customer, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // The check-then-insert in the service narrows the window; the index
            // closes it. Either way the caller gets a conflict, never a 500.
            throw new UniqueConstraintViolationException("The customer conflicts with an existing one.", ex);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresUniqueViolationSqlState };
}

/// <summary>
/// Notes are append-only, so this has a list and an add and nothing else.
/// </summary>
internal sealed class CustomerNoteRepository : ICustomerNoteRepository
{
    private readonly ControlPlaneDbContext _context;

    public CustomerNoteRepository(ControlPlaneDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyCollection<CustomerNote>> ListAsync(
        Guid customerId,
        int limit,
        CancellationToken cancellationToken) =>
        await _context.CustomerNotes
            .AsNoTracking()
            .Where(note => note.CustomerId == customerId)
            .OrderByDescending(note => note.CreatedAt)
            .Take(limit)
            .ToArrayAsync(cancellationToken);

    public async Task AddAsync(CustomerNote note, CancellationToken cancellationToken) =>
        await _context.CustomerNotes.AddAsync(note, cancellationToken);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => _context.SaveChangesAsync(cancellationToken);
}
