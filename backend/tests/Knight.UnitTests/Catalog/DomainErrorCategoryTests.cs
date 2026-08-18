using Catalog.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Catalog;

/// <summary>
/// The 400-vs-409 split is decided in the domain, not by the HTTP layer reading
/// message strings. These tests pin the category directly on the thrown exception so
/// the guarantee holds even if the API mapping is ever rewritten: malformed input
/// (a negative price, a self-contradictory selection rule) is
/// <see cref="DomainErrorCategory.Validation"/>, never <c>Conflict</c>.
/// </summary>
public sealed class DomainErrorCategoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static void AssertValidationCategory(Action act)
    {
        var exception = Assert.Throws<DomainException>(act);

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
        Assert.NotEqual(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void ProductCreate_WithNegativeBasePrice_IsValidationCategory()
    {
        AssertValidationCategory(() => Product.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), "Product A", null, null,
            ProductStatus.Draft, -1m, true, true, 0));
    }

    [Fact]
    public void ProductChangeBasePrice_WithNegativePrice_IsValidationCategory()
    {
        var product = Product.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), "Product A", null, null,
            ProductStatus.Draft, 10m, true, true, 0);

        AssertValidationCategory(() => product.ChangeBasePrice(-1m, Now));
    }

    [Fact]
    public void ModifierCreate_WithNegativePriceDelta_IsValidationCategory()
    {
        AssertValidationCategory(() => Modifier.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), "Modifier A", -1m, true, 0));
    }

    [Fact]
    public void ModifierGroupCreate_WithNegativeMinimumSelections_IsValidationCategory()
    {
        AssertValidationCategory(() => ModifierGroup.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), "Group A", false, -1, 3, 0));
    }

    [Fact]
    public void ModifierGroupCreate_WithMaximumBelowMinimum_IsValidationCategory()
    {
        AssertValidationCategory(() => ModifierGroup.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), "Group A", false, 2, 1, 0));
    }

    [Fact]
    public void ModifierGroupCreate_WithRequiredAndZeroMinimum_IsValidationCategory()
    {
        AssertValidationCategory(() => ModifierGroup.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), "Group A", true, 0, 3, 0));
    }

    [Fact]
    public void SlugNormalization_WithNoAlphanumericContent_IsValidationCategory()
    {
        AssertValidationCategory(() => Product.NormalizeSlug("---"));
    }
}
