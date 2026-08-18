using Catalog.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Catalog;

/// <summary>
/// Slug normalization is the uniqueness constraint (the unique indexes are
/// declared on the normalized column), so its behavior is pinned here directly.
/// The normalizer itself is internal; <see cref="Category.NormalizeSlug"/> and
/// <see cref="Product.NormalizeSlug"/> are the public entry points and must
/// never diverge.
/// </summary>
public sealed class SlugNormalizationTests
{
    [Theory]
    [InlineData("Category A", "category-a")]
    [InlineData("CATEGORY A", "category-a")]
    [InlineData("  Category   A  ", "category-a")]
    [InlineData("Category_A", "category-a")]
    [InlineData("Category/A", "category-a")]
    [InlineData("Category!!!A", "category-a")]
    [InlineData("--Category-A--", "category-a")]
    [InlineData("Product A 2", "product-a-2")]
    [InlineData("already-normalized", "already-normalized")]
    public void Normalize_LowercasesCollapsesSeparatorsAndStripsInvalidCharacters(string raw, string expected)
    {
        Assert.Equal(expected, Category.NormalizeSlug(raw));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("---")]
    [InlineData("///")]
    public void Normalize_WithNothingUsableRemaining_ThrowsDomainException(string raw)
    {
        Assert.Throws<DomainException>(() => Category.NormalizeSlug(raw));
    }

    [Fact]
    public void Normalize_TruncatesToTheMaximumIndexedLength()
    {
        var raw = new string('a', 200);

        var normalized = Category.NormalizeSlug(raw);

        Assert.Equal(150, normalized.Length);
    }

    [Fact]
    public void Normalize_IsIdenticalForCategoryAndProduct()
    {
        const string raw = "  Mixed CASE / Value!! ";

        Assert.Equal(Category.NormalizeSlug(raw), Product.NormalizeSlug(raw));
    }
}
