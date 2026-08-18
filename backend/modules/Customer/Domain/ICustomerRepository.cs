namespace Customer.Domain;

public sealed record CustomerListFilter(
    string? SearchTerm = null,
    CustomerStatus? Status = null);

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Customer> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CustomerListFilter filter,
        CancellationToken cancellationToken);

    Task AddAsync(Customer customer, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
