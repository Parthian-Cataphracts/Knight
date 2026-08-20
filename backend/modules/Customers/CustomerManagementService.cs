using Customers.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;

namespace Customers;

/// <summary>
/// Customer lifecycle. Two rules are enforced here rather than in the aggregate
/// because both need to see other rows: contact emails are unique across
/// customers, and a lookup that the isolation filter hides must read as "not
/// found" rather than "forbidden", so a customer-scoped caller cannot use error
/// codes to discover that another customer exists
/// (docs/authorization.md section 4).
/// </summary>
internal sealed class CustomerManagementService : ICustomerManagementService
{
    private const int MaxPageSize = 100;

    private readonly ICustomerRepository _customers;
    private readonly IAuditTrail _audit;
    private readonly IDateTimeProvider _clock;

    public CustomerManagementService(ICustomerRepository customers, IAuditTrail audit, IDateTimeProvider clock)
    {
        _customers = customers;
        _audit = audit;
        _clock = clock;
    }

    public async Task<Customer> CreateAsync(CreateCustomerInput input, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var normalizedEmail = CustomerNormalization.NormalizeEmail(input.ContactEmail);

        if (await _customers.ExistsWithContactEmailAsync(normalizedEmail, excludingId: null, cancellationToken))
        {
            throw new ConflictException($"A customer with contact email '{normalizedEmail}' already exists.");
        }

        var customer = Customer.Create(Guid.NewGuid(), now, input.Name, input.ContactEmail);
        customer.UpdateProfile(input.Name, input.LegalName, input.ContactEmail, input.Phone, now);
        customer.SetNotes(input.Notes, now);

        await _customers.AddAsync(customer, cancellationToken);
        await _customers.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "customer.created",
            nameof(Customer),
            customer.Id.ToString(),
            customer.Id,
            cancellationToken,
            newValue: Snapshot(customer));

        return customer;
    }

    public Task<Customer?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        _customers.GetByIdAsync(id, cancellationToken);

    public async Task<CustomerPage> ListAsync(CustomerListQuery query, CancellationToken cancellationToken)
    {
        var page = query.Page < 1 ? 1 : query.Page;
        var pageSize = query.PageSize is < 1 or > MaxPageSize ? 25 : query.PageSize;

        var (items, total) = await _customers.ListAsync(page, pageSize, query.Status, query.Search, cancellationToken);
        return new CustomerPage(items, page, pageSize, total);
    }

    public async Task<Customer> UpdateAsync(Guid id, UpdateCustomerInput input, CancellationToken cancellationToken)
    {
        var customer = await RequireAsync(id, cancellationToken);
        var before = Snapshot(customer);
        var now = _clock.UtcNow;

        var normalizedEmail = CustomerNormalization.NormalizeEmail(input.ContactEmail);
        if (await _customers.ExistsWithContactEmailAsync(normalizedEmail, excludingId: customer.Id, cancellationToken))
        {
            throw new ConflictException($"A customer with contact email '{normalizedEmail}' already exists.");
        }

        customer.UpdateProfile(input.Name, input.LegalName, input.ContactEmail, input.Phone, now);
        customer.SetNotes(input.Notes, now);
        await _customers.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "customer.updated",
            nameof(Customer),
            customer.Id.ToString(),
            customer.Id,
            cancellationToken,
            before,
            Snapshot(customer));

        return customer;
    }

    public async Task<Customer> SetRetentionOverrideAsync(Guid id, int? days, CancellationToken cancellationToken)
    {
        var customer = await RequireAsync(id, cancellationToken);
        var previous = customer.DataRetentionOverrideDays;

        customer.SetDataRetentionOverride(days, _clock.UtcNow);
        await _customers.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            "customer.retention.changed",
            nameof(Customer),
            customer.Id.ToString(),
            customer.Id,
            cancellationToken,
            previousValue: new { retentionDays = previous },
            newValue: new { retentionDays = days });

        return customer;
    }

    public Task<Customer> ActivateAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "customer.activated", (customer, now) => customer.Activate(now), cancellationToken);

    public Task<Customer> SuspendAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "customer.suspended", (customer, now) => customer.Suspend(now), cancellationToken);

    public Task<Customer> ArchiveAsync(Guid id, CancellationToken cancellationToken) =>
        TransitionAsync(id, "customer.archived", (customer, now) => customer.Archive(now), cancellationToken);

    private async Task<Customer> TransitionAsync(
        Guid id,
        string action,
        Action<Customer, DateTimeOffset> transition,
        CancellationToken cancellationToken)
    {
        var customer = await RequireAsync(id, cancellationToken);
        var before = Snapshot(customer);

        transition(customer, _clock.UtcNow);
        await _customers.SaveChangesAsync(cancellationToken);

        await _audit.RecordAsync(
            action,
            nameof(Customer),
            customer.Id.ToString(),
            customer.Id,
            cancellationToken,
            before,
            Snapshot(customer));

        return customer;
    }

    private async Task<Customer> RequireAsync(Guid id, CancellationToken cancellationToken) =>
        await _customers.GetByIdAsync(id, cancellationToken)
        ?? throw new NotFoundException($"Customer '{id}' was not found.");

    private static object Snapshot(Customer customer) => new
    {
        customer.Name,
        customer.LegalName,
        customer.ContactEmail,
        customer.Phone,
        Status = customer.Status.ToString(),
        customer.Notes,
    };
}
