using Catalog.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Catalog;

public sealed class ModifierGroupTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ModifierGroup CreateValidGroup() =>
        ModifierGroup.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Group A", isRequired: false, minSelections: 0, maxSelections: 3, displayOrder: 0);

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            ModifierGroup.Create(Guid.NewGuid(), Now, Guid.Empty, "Group A", false, 0, 1, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ThrowsDomainException(string name)
    {
        Assert.Throws<DomainException>(() =>
            ModifierGroup.Create(Guid.NewGuid(), Now, Guid.NewGuid(), name, false, 0, 1, 0));
    }

    /// <summary>
    /// The three invalid selection-rule shapes, exactly as specified: a negative
    /// minimum, a maximum below the minimum, and a required group that permits
    /// selecting nothing.
    /// </summary>
    [Theory]
    [InlineData(false, -1, 3)]
    [InlineData(false, -5, 0)]
    [InlineData(false, 2, 1)]
    [InlineData(false, 1, 0)]
    [InlineData(true, 0, 3)]
    [InlineData(true, 0, 0)]
    public void Create_WithInvalidSelectionRules_ThrowsDomainException(bool isRequired, int min, int max)
    {
        Assert.Throws<DomainException>(() =>
            ModifierGroup.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Group A", isRequired, min, max, 0));
    }

    [Theory]
    [InlineData(false, 0, 0)]
    [InlineData(false, 0, 3)]
    [InlineData(false, 2, 2)]
    [InlineData(true, 1, 3)]
    [InlineData(true, 1, 1)]
    [InlineData(true, 3, 3)]
    public void Create_WithValidSelectionRules_Succeeds(bool isRequired, int min, int max)
    {
        var group = ModifierGroup.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Group A", isRequired, min, max, 0);

        Assert.Equal(isRequired, group.IsRequired);
        Assert.Equal(min, group.MinSelections);
        Assert.Equal(max, group.MaxSelections);
    }

    [Theory]
    [InlineData(false, -1, 3)]
    [InlineData(false, 2, 1)]
    [InlineData(true, 0, 3)]
    public void UpdateSelectionRules_WithInvalidCombination_ThrowsDomainExceptionAndLeavesTheGroupUnchanged(
        bool isRequired,
        int min,
        int max)
    {
        var group = CreateValidGroup();

        Assert.Throws<DomainException>(() => group.UpdateSelectionRules(isRequired, min, max, Now.AddMinutes(1)));

        Assert.False(group.IsRequired);
        Assert.Equal(0, group.MinSelections);
        Assert.Equal(3, group.MaxSelections);
    }

    [Fact]
    public void UpdateSelectionRules_WithValidRequiredConfiguration_Applies()
    {
        var group = CreateValidGroup();

        group.UpdateSelectionRules(isRequired: true, min: 1, max: 3, Now.AddMinutes(1));

        Assert.True(group.IsRequired);
        Assert.Equal(1, group.MinSelections);
        Assert.Equal(3, group.MaxSelections);
        Assert.Equal(Now.AddMinutes(1), group.UpdatedAt);
    }

    [Fact]
    public void UpdateDetails_WithInvalidSelectionRules_ThrowsDomainException()
    {
        var group = CreateValidGroup();

        Assert.Throws<DomainException>(() => group.UpdateDetails("Group B", true, 0, 3, 0, Now.AddMinutes(1)));
    }

    [Fact]
    public void Rename_UpdatesTheName()
    {
        var group = CreateValidGroup();

        group.Rename("Group B", Now.AddMinutes(1));

        Assert.Equal("Group B", group.Name);
    }
}
