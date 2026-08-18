using Customer.Domain;
using Knight.Domain.Exceptions;

namespace Knight.UnitTests.Customer;

using CustomerEntity = global::Customer.Domain.Customer;

public sealed class CustomerLifecycleTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void Archive_ActiveCustomer_TransitionsToArchived()
    {
        var customer = CustomerEntity.Create(
            Guid.NewGuid(),
            _now,
            _tenantId,
            "Active Customer",
            "+15551234567",
            null);

        var archiveTime = _now.AddMinutes(15);
        customer.Archive(archiveTime);

        Assert.Equal(CustomerStatus.Archived, customer.Status);
        Assert.Equal(archiveTime, customer.ArchivedAt);
        Assert.Equal(archiveTime, customer.UpdatedAt);
    }

    [Fact]
    public void Archive_AlreadyArchivedCustomer_ThrowsConflictException()
    {
        var customer = CustomerEntity.Create(
            Guid.NewGuid(),
            _now,
            _tenantId,
            "Customer",
            "+15551234567",
            null);

        customer.Archive(_now);

        var ex = Assert.Throws<DomainException>(() => customer.Archive(_now.AddMinutes(5)));
        Assert.Equal(DomainErrorCategory.Conflict, ex.Category);
        Assert.Contains("already archived", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Restore_ArchivedCustomer_TransitionsToActive()
    {
        var customer = CustomerEntity.Create(
            Guid.NewGuid(),
            _now,
            _tenantId,
            "Customer",
            "+15551234567",
            null);

        customer.Archive(_now);
        var restoreTime = _now.AddHours(1);
        customer.Restore(restoreTime);

        Assert.Equal(CustomerStatus.Active, customer.Status);
        Assert.Null(customer.ArchivedAt);
        Assert.Equal(restoreTime, customer.UpdatedAt);
    }

    [Fact]
    public void Restore_AlreadyActiveCustomer_ThrowsConflictException()
    {
        var customer = CustomerEntity.Create(
            Guid.NewGuid(),
            _now,
            _tenantId,
            "Customer",
            "+15551234567",
            null);

        var ex = Assert.Throws<DomainException>(() => customer.Restore(_now.AddMinutes(5)));
        Assert.Equal(DomainErrorCategory.Conflict, ex.Category);
        Assert.Contains("already active", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
