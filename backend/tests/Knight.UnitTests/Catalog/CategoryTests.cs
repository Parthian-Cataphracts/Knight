using Catalog.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Catalog;

public sealed class CategoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            Category.Create(Guid.NewGuid(), Now, Guid.Empty, "Category A", null, null, true, 0));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ThrowsDomainException(string name)
    {
        Assert.Throws<DomainException>(() =>
            Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), name, null, null, true, 0));
    }

    [Fact]
    public void Create_WithNameExceedingMaxLength_ThrowsDomainException()
    {
        var tooLong = new string('a', 151);

        Assert.Throws<DomainException>(() =>
            Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), tooLong, null, null, true, 0));
    }

    [Fact]
    public void Create_WithDescriptionExceedingMaxLength_ThrowsDomainException()
    {
        var tooLong = new string('a', 1001);

        Assert.Throws<DomainException>(() =>
            Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, tooLong, true, 0));
    }

    [Fact]
    public void Create_WithoutSlug_DerivesNormalizedSlugFromName()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, null, true, 0);

        Assert.Equal("category-a", category.Slug);
    }

    [Fact]
    public void Create_TrimsNameAndKeepsDisplayForm()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "  Category A  ", null, null, true, 0);

        Assert.Equal("Category A", category.Name);
    }

    [Fact]
    public void Create_WithBlankDescription_StoresNull()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, "   ", true, 0);

        Assert.Null(category.Description);
    }

    [Fact]
    public void Rename_UpdatesNameWithoutChangingSlug()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, null, true, 0);

        category.Rename("Category B", Now.AddMinutes(1));

        Assert.Equal("Category B", category.Name);
        Assert.Equal("category-a", category.Slug);
        Assert.Equal(Now.AddMinutes(1), category.UpdatedAt);
    }

    [Fact]
    public void Rename_WithBlankName_ThrowsDomainException()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, null, true, 0);

        Assert.Throws<DomainException>(() => category.Rename("  ", Now));
    }

    [Fact]
    public void ChangeSlug_NormalizesTheSuppliedValue()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, null, true, 0);

        category.ChangeSlug("  Category   B!!  ", Now.AddMinutes(1));

        Assert.Equal("category-b", category.Slug);
    }

    [Fact]
    public void ChangeSlug_WithValueThatNormalizesToNothing_ThrowsDomainException()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, null, true, 0);

        Assert.Throws<DomainException>(() => category.ChangeSlug("!!!", Now));
    }

    [Fact]
    public void SetVisibility_TogglesTheFlag()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, null, true, 0);

        category.SetVisibility(false, Now.AddMinutes(1));

        Assert.False(category.IsVisible);
    }

    [Fact]
    public void UpdateDetails_ReplacesEveryEditableField()
    {
        var category = Category.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "Category A", null, null, true, 0);

        category.UpdateDetails("Category B", "custom-slug", "Description text.", false, 7, Now.AddMinutes(1));

        Assert.Equal("Category B", category.Name);
        Assert.Equal("custom-slug", category.Slug);
        Assert.Equal("Description text.", category.Description);
        Assert.False(category.IsVisible);
        Assert.Equal(7, category.DisplayOrder);
    }
}
