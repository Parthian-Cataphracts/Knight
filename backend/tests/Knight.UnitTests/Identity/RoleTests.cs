using Identity.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Identity;

public sealed class RoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => Role.Create(Guid.NewGuid(), Now, Guid.Empty, "Manager"));
    }

    [Theory]
    [InlineData("Manager", "MANAGER")]
    [InlineData("  manager  ", "MANAGER")]
    [InlineData("MANAGER", "MANAGER")]
    public void Create_NormalizesNameForComparisonRegardlessOfCasingOrWhitespace(string rawName, string expectedNormalized)
    {
        var role = Role.Create(Guid.NewGuid(), Now, Guid.NewGuid(), rawName);

        Assert.Equal(expectedNormalized, role.NormalizedName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithEmptyName_ThrowsDomainException(string name)
    {
        Assert.Throws<DomainException>(() => Role.Create(Guid.NewGuid(), Now, Guid.NewGuid(), name));
    }

    [Fact]
    public void Create_WithNameExceedingMaxLength_ThrowsDomainException()
    {
        var tooLong = new string('a', 101);

        Assert.Throws<DomainException>(() => Role.Create(Guid.NewGuid(), Now, Guid.NewGuid(), tooLong));
    }

    [Fact]
    public void Rename_UpdatesNameAndNormalizedName()
    {
        var role = Role.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Manager");

        role.Rename("Supervisor", Now.AddMinutes(1));

        Assert.Equal("Supervisor", role.Name);
        Assert.Equal("SUPERVISOR", role.NormalizedName);
    }
}
