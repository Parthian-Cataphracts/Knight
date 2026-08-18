using Customer.Domain;
using Knight.Domain.Exceptions;

namespace Knight.UnitTests.Customer;

using CustomerEntity = global::Customer.Domain.Customer;

public sealed class CustomerTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void Create_ValidWithPhoneAndEmail_Succeeds()
    {
        var id = Guid.NewGuid();
        var customer = CustomerEntity.Create(
            id,
            _now,
            _tenantId,
            "John Doe",
            "+1 (555) 123-4567",
            "JOHN@example.com");

        Assert.Equal(id, customer.Id);
        Assert.Equal(_tenantId, customer.TenantId);
        Assert.Equal("John Doe", customer.DisplayName);
        Assert.Equal("+1 (555) 123-4567", customer.Phone);
        Assert.Equal("+15551234567", customer.NormalizedPhone);
        Assert.Equal("JOHN@example.com", customer.Email);
        Assert.Equal("john@example.com", customer.NormalizedEmail);
        Assert.Equal(CustomerStatus.Active, customer.Status);
        Assert.Null(customer.ArchivedAt);
    }

    [Fact]
    public void Create_WithOnlyPhone_Succeeds()
    {
        var customer = CustomerEntity.Create(
            Guid.NewGuid(),
            _now,
            _tenantId,
            "Phone User",
            "+989123456789",
            null);

        Assert.Equal("Phone User", customer.DisplayName);
        Assert.Equal("+989123456789", customer.NormalizedPhone);
        Assert.Null(customer.Email);
        Assert.Null(customer.NormalizedEmail);
    }

    [Fact]
    public void Create_WithOnlyEmail_Succeeds()
    {
        var customer = CustomerEntity.Create(
            Guid.NewGuid(),
            _now,
            _tenantId,
            "Email User",
            null,
            "user@test.org");

        Assert.Equal("Email User", customer.DisplayName);
        Assert.Null(customer.Phone);
        Assert.Equal("user@test.org", customer.NormalizedEmail);
    }

    [Fact]
    public void Create_MissingBothPhoneAndEmail_ThrowsValidationException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            CustomerEntity.Create(
                Guid.NewGuid(),
                _now,
                _tenantId,
                "No Contact",
                null,
                null));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Create_EmptyDisplayName_ThrowsValidationException(string? name)
    {
        var ex = Assert.Throws<DomainException>(() =>
            CustomerEntity.Create(
                Guid.NewGuid(),
                _now,
                _tenantId,
                name!,
                "+15551234567",
                null));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void Create_EmptyTenantId_ThrowsValidationException()
    {
        var ex = Assert.Throws<DomainException>(() =>
            CustomerEntity.Create(
                Guid.NewGuid(),
                _now,
                Guid.Empty,
                "Valid Name",
                "+15551234567",
                null));

        Assert.Equal(DomainErrorCategory.Validation, ex.Category);
    }

    [Fact]
    public void UpdateDetails_ValidChanges_UpdatesPropertiesAndMarksUpdated()
    {
        var customer = CustomerEntity.Create(
            Guid.NewGuid(),
            _now,
            _tenantId,
            "Initial Name",
            "+15551112233",
            null);

        var later = _now.AddHours(2);
        customer.UpdateDetails("Updated Name", null, "updated@example.com", later);

        Assert.Equal("Updated Name", customer.DisplayName);
        Assert.Null(customer.Phone);
        Assert.Null(customer.NormalizedPhone);
        Assert.Equal("updated@example.com", customer.Email);
        Assert.Equal("updated@example.com", customer.NormalizedEmail);
        Assert.Equal(later, customer.UpdatedAt);
    }
}
