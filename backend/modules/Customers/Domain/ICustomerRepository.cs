namespace Customers.Domain;

/// <summary>
/// Persistence contract for control-plane customers. Implementations apply the
/// customer-scoping filter of the current principal, so a customer-scoped caller
/// can never read another customer through this interface
/// (docs/authorization.md section 3).
/// </summary>
public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> ExistsWithContactEmailAsync(string normalizedEmail, Guid? excludingId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Customer> Items, long TotalCount)> ListAsync(
        int page,
        int pageSize,
        CustomerStatus? status,
        string? search,
        CancellationToken cancellationToken);

    Task AddAsync(Customer customer, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
