using Catalog.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Catalog;

public sealed class ModifierTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Modifier CreateModifier(decimal priceDelta = 1m) =>
        Modifier.Create(Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), "Modifier A", priceDelta, isAvailable: true, displayOrder: 0);

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            Modifier.Create(Guid.NewGuid(), Now, Guid.Empty, Guid.NewGuid(), "Modifier A", 1m, true, 0));
    }

    [Fact]
    public void Create_WithEmptyModifierGroupId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            Modifier.Create(Guid.NewGuid(), Now, Guid.NewGuid(), Guid.Empty, "Modifier A", 1m, true, 0));
    }

    [Fact]
    public void Create_WithNegativePriceDelta_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => CreateModifier(-0.01m));
        Assert.Throws<DomainException>(() => CreateModifier(-1m));
    }

    [Fact]
    public void Create_WithZeroPriceDelta_Succeeds()
    {
        var modifier = CreateModifier(0m);

        Assert.Equal(0m, modifier.PriceDelta);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ThrowsDomainException(string name)
    {
        Assert.Throws<DomainException>(() =>
            Modifier.Create(Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), name, 1m, true, 0));
    }

    [Fact]
    public void UpdateDetails_WithNegativePriceDelta_ThrowsDomainException()
    {
        var modifier = CreateModifier();

        Assert.Throws<DomainException>(() => modifier.UpdateDetails("Modifier A", -1m, true, 0, Now));
    }

    [Fact]
    public void SetAvailability_TogglesTheFlag()
    {
        var modifier = CreateModifier();

        modifier.SetAvailability(false, Now.AddMinutes(1));

        Assert.False(modifier.IsAvailable);
    }
}
