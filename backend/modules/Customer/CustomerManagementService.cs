using Customer.Domain;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Knight.Domain.Exceptions;

namespace Customer;

public sealed record CreateCustomerInput(
    string DisplayName,
    string? Phone = null,
    string? Email = null);

public sealed record UpdateCustomerInput(
    string DisplayName,
    string? Phone = null,
    string? Email = null);

public interface ICustomerManagementService
{
    Task<Customer.Domain.Customer?> GetByIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<Customer.Domain.Customer> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CustomerListFilter filter,
        CancellationToken cancellationToken);

    Task<Customer.Domain.Customer> CreateAsync(
        Guid tenantId,
        CreateCustomerInput input,
        CancellationToken cancellationToken);

    Task<Customer.Domain.Customer> UpdateAsync(
        Guid tenantId,
        Guid customerId,
        UpdateCustomerInput input,
        CancellationToken cancellationToken);

    Task<Customer.Domain.Customer> ArchiveAsync(
        Guid tenantId,
        Guid customerId,
        CancellationToken cancellationToken);

    Task<Customer.Domain.Customer> RestoreAsync(
        Guid tenantId,
        Guid customerId,
        CancellationToken cancellationToken);
}

internal sealed class CustomerManagementService : ICustomerManagementService
{
    private readonly ICustomerRepository _repository;
    private readonly CustomerAuditRecorder _audit;
    private readonly IDateTimeProvider _dateTimeProvider;

    public CustomerManagementService(
        ICustomerRepository repository,
        CustomerAuditRecorder audit,
        IDateTimeProvider dateTimeProvider)
    {
        _repository = repository;
        _audit = audit;
        _dateTimeProvider = dateTimeProvider;
    }

    public Task<Customer.Domain.Customer?> GetByIdAsync(Guid tenantId, Guid customerId, CancellationToken cancellationToken)
    {
        return _repository.GetByIdAsync(tenantId, customerId, cancellationToken);
    }

    public Task<(IReadOnlyCollection<Customer.Domain.Customer> Items, long TotalCount)> ListAsync(
        Guid tenantId,
        int page,
        int pageSize,
        CustomerListFilter filter,
        CancellationToken cancellationToken)
    {
        if (page < 1) page = 1;
        if (pageSize < 1) pageSize = 20;
        if (pageSize > 100) pageSize = 100;

        return _repository.ListAsync(tenantId, page, pageSize, filter, cancellationToken);
    }

    public async Task<Customer.Domain.Customer> CreateAsync(
        Guid tenantId,
        CreateCustomerInput input,
        CancellationToken cancellationToken)
    {
        var now = _dateTimeProvider.UtcNow;
        var customerId = Guid.NewGuid();

        var customer = Customer.Domain.Customer.Create(
            customerId,
            now,
            tenantId,
            input.DisplayName,
            input.Phone,
            input.Email);

        await _repository.AddAsync(customer, cancellationToken);
        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "CustomerCreated",
            tenantId,
            customerId,
            cancellationToken,
            nonPiiMetadata: new Dictionary<string, string>
            {
                ["hasPhone"] = (!string.IsNullOrWhiteSpace(customer.NormalizedPhone)).ToString(),
                ["hasEmail"] = (!string.IsNullOrWhiteSpace(customer.NormalizedEmail)).ToString()
            });

        return customer;
    }

    public async Task<Customer.Domain.Customer> UpdateAsync(
        Guid tenantId,
        Guid customerId,
        UpdateCustomerInput input,
        CancellationToken cancellationToken)
    {
        var customer = await _repository.GetByIdAsync(tenantId, customerId, cancellationToken);
        if (customer is null)
        {
            throw new NotFoundException($"Customer with ID '{customerId}' was not found.");
        }

        var now = _dateTimeProvider.UtcNow;
        customer.UpdateDetails(input.DisplayName, input.Phone, input.Email, now);

        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "CustomerUpdated",
            tenantId,
            customerId,
            cancellationToken,
            nonPiiMetadata: new Dictionary<string, string>
            {
                ["hasPhone"] = (!string.IsNullOrWhiteSpace(customer.NormalizedPhone)).ToString(),
                ["hasEmail"] = (!string.IsNullOrWhiteSpace(customer.NormalizedEmail)).ToString()
            });

        return customer;
    }

    public async Task<Customer.Domain.Customer> ArchiveAsync(
        Guid tenantId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var customer = await _repository.GetByIdAsync(tenantId, customerId, cancellationToken);
        if (customer is null)
        {
            throw new NotFoundException($"Customer with ID '{customerId}' was not found.");
        }

        var now = _dateTimeProvider.UtcNow;
        customer.Archive(now);

        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("CustomerArchived", tenantId, customerId, cancellationToken);

        return customer;
    }

    public async Task<Customer.Domain.Customer> RestoreAsync(
        Guid tenantId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        var customer = await _repository.GetByIdAsync(tenantId, customerId, cancellationToken);
        if (customer is null)
        {
            throw new NotFoundException($"Customer with ID '{customerId}' was not found.");
        }

        var now = _dateTimeProvider.UtcNow;
        customer.Restore(now);

        await _repository.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync("CustomerRestored", tenantId, customerId, cancellationToken);

        return customer;
    }
}
