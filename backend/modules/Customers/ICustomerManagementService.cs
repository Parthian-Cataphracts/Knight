using Customers.Domain;

namespace Customers;

public sealed record CreateCustomerInput(string Name, string? LegalName, string ContactEmail, string? Phone, string? Notes);

public sealed record UpdateCustomerInput(string Name, string? LegalName, string ContactEmail, string? Phone, string? Notes);

public sealed record CustomerListQuery(int Page, int PageSize, CustomerStatus? Status, string? Search);

public sealed record CustomerPage(IReadOnlyCollection<Customer> Items, int Page, int PageSize, long TotalCount);

/// <summary>
/// Customer lifecycle for the dashboard. Every mutation writes an audit entry
/// and every read is subject to the customer-isolation filter, so a
/// customer-scoped caller sees exactly one customer: their own.
/// </summary>
public interface ICustomerManagementService
{
    Task<Customer> CreateAsync(CreateCustomerInput input, CancellationToken cancellationToken);

    Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken);

    Task<CustomerPage> ListAsync(CustomerListQuery query, CancellationToken cancellationToken);

    Task<Customer> UpdateAsync(Guid id, UpdateCustomerInput input, CancellationToken cancellationToken);

    /// <summary>
    /// Sets or clears the customer's negotiated data-retention window, in days.
    /// Audited with both the old and the new value: shortening how long somebody
    /// else's data is kept is a decision somebody has to be answerable for.
    /// </summary>
    Task<Customer> SetRetentionOverrideAsync(Guid id, int? days, CancellationToken cancellationToken);

    Task<Customer> ActivateAsync(Guid id, CancellationToken cancellationToken);

    Task<Customer> SuspendAsync(Guid id, CancellationToken cancellationToken);

    Task<Customer> ArchiveAsync(Guid id, CancellationToken cancellationToken);
}
