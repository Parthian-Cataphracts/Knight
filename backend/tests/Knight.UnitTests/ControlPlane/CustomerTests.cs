using Customers.Domain;
using Knight.Domain.Exceptions;
using Xunit;

// The frozen store module also declares a `Customer` namespace, so the
// control-plane aggregate is aliased here until that module leaves the solution
// (docs/migration-plan.md, "Terminology collision").
using ControlPlaneCustomer = Customers.Domain.Customer;

namespace Knight.UnitTests.ControlPlane;

public sealed class ControlPlaneCustomerTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ControlPlaneCustomer CreateCustomer() =>
        ControlPlaneCustomer.Create(Guid.NewGuid(), Now, "Cafe One", "Owner@Cafe1.IR");

    [Fact]
    public void Create_StartsAsProspectWithNormalizedEmail()
    {
        var customer = CreateCustomer();

        Assert.Equal(CustomerStatus.Prospect, customer.Status);
        Assert.Equal("owner@cafe1.ir", customer.ContactEmail);
        Assert.False(customer.IsOperable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-email")]
    public void Create_WithInvalidEmail_Throws(string email)
    {
        var exception = Assert.Throws<DomainException>(() =>
            ControlPlaneCustomer.Create(Guid.NewGuid(), Now, "Cafe One", email));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }

    [Fact]
    public void Activate_MakesTheCustomerOperable()
    {
        var customer = CreateCustomer();

        customer.Activate(Now);

        Assert.Equal(CustomerStatus.Active, customer.Status);
        Assert.True(customer.IsOperable);
    }

    [Fact]
    public void Suspend_FromProspect_IsRejected()
    {
        var customer = CreateCustomer();

        var exception = Assert.Throws<DomainException>(() => customer.Suspend(Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void SuspendedCustomer_CanBeReactivated()
    {
        var customer = CreateCustomer();
        customer.Activate(Now);
        customer.Suspend(Now);

        customer.Activate(Now);

        Assert.Equal(CustomerStatus.Active, customer.Status);
    }

    [Fact]
    public void Archive_IsTerminal()
    {
        var customer = CreateCustomer();
        customer.Activate(Now);
        customer.Archive(Now);

        Assert.Equal(CustomerStatus.Archived, customer.Status);
        Assert.Throws<DomainException>(() => customer.Activate(Now));
        Assert.Throws<DomainException>(() => customer.UpdateProfile("New", null, "owner@cafe1.ir", null, Now));
        Assert.Throws<DomainException>(() => customer.SetNotes("note", Now));
    }

    [Fact]
    public void UpdateProfile_NormalizesContactFields()
    {
        var customer = CreateCustomer();

        customer.UpdateProfile("Cafe One", "  Cafe One LLC  ", "Billing@Cafe1.IR", " +98 21 1234 5678 ", Now);

        Assert.Equal("Cafe One LLC", customer.LegalName);
        Assert.Equal("billing@cafe1.ir", customer.ContactEmail);
        Assert.Equal("+98 21 1234 5678", customer.Phone);
        Assert.Equal(Now, customer.UpdatedAt);
    }

    [Fact]
    public void UpdateProfile_WithTooFewPhoneDigits_Throws()
    {
        var customer = CreateCustomer();

        Assert.Throws<DomainException>(() =>
            customer.UpdateProfile("Cafe One", null, "owner@cafe1.ir", "123", Now));
    }
}
