using Catalog.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Catalog;

public sealed class ProductTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Product CreateProduct(decimal basePrice = 10m, ProductStatus status = ProductStatus.Draft) =>
        Product.Create(
            Guid.NewGuid(),
            Now,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Product A",
            null,
            null,
            status,
            basePrice,
            isVisible: true,
            isAvailable: true,
            displayOrder: 0);

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => Product.Create(
            Guid.NewGuid(), Now, Guid.Empty, Guid.NewGuid(), "Product A", null, null, ProductStatus.Draft, 10m, true, true, 0));
    }

    [Fact]
    public void Create_WithEmptyCategoryId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => Product.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), Guid.Empty, "Product A", null, null, ProductStatus.Draft, 10m, true, true, 0));
    }

    [Fact]
    public void Create_WithNegativeBasePrice_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => CreateProduct(-0.01m));
        Assert.Throws<DomainException>(() => CreateProduct(-1m));
        Assert.Throws<DomainException>(() => CreateProduct(-1000m));
    }

    [Fact]
    public void Create_WithZeroBasePrice_Succeeds()
    {
        var product = CreateProduct(0m);

        Assert.Equal(0m, product.BasePrice);
    }

    [Fact]
    public void Create_PreservesDecimalScale()
    {
        var product = CreateProduct(19.99m);

        Assert.Equal(19.99m, product.BasePrice);
    }

    [Fact]
    public void Create_WithoutSlug_DerivesNormalizedSlugFromName()
    {
        var product = CreateProduct();

        Assert.Equal("product-a", product.Slug);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankName_ThrowsDomainException(string name)
    {
        Assert.Throws<DomainException>(() => Product.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), name, null, null, ProductStatus.Draft, 10m, true, true, 0));
    }

    [Fact]
    public void Activate_MovesADraftToActive()
    {
        var product = CreateProduct(status: ProductStatus.Draft);

        product.Activate(Now.AddMinutes(1));

        Assert.Equal(ProductStatus.Active, product.Status);
        Assert.Equal(Now.AddMinutes(1), product.UpdatedAt);
    }

    [Fact]
    public void Archive_MovesAnActiveProductToArchived()
    {
        var product = CreateProduct(status: ProductStatus.Active);

        product.Archive(Now.AddMinutes(1));

        Assert.Equal(ProductStatus.Archived, product.Status);
    }

    [Fact]
    public void Activate_AfterArchive_ReturnsTheProductToActive()
    {
        var product = CreateProduct(status: ProductStatus.Active);

        product.Archive(Now.AddMinutes(1));
        product.Activate(Now.AddMinutes(2));

        Assert.Equal(ProductStatus.Active, product.Status);
    }

    [Fact]
    public void ChangeBasePrice_WithNegativeValue_ThrowsDomainException()
    {
        var product = CreateProduct();

        Assert.Throws<DomainException>(() => product.ChangeBasePrice(-5m, Now));
    }

    [Fact]
    public void ChangeCategory_WithEmptyCategoryId_ThrowsDomainException()
    {
        var product = CreateProduct();

        Assert.Throws<DomainException>(() => product.ChangeCategory(Guid.Empty, Now));
    }

    [Fact]
    public void SetVisibilityAndAvailability_AreIndependentFlags()
    {
        var product = CreateProduct();

        product.SetVisibility(false, Now.AddMinutes(1));
        product.SetAvailability(false, Now.AddMinutes(2));

        Assert.False(product.IsVisible);
        Assert.False(product.IsAvailable);

        product.SetVisibility(true, Now.AddMinutes(3));

        Assert.True(product.IsVisible);
        Assert.False(product.IsAvailable);
    }

    [Fact]
    public void UpdateDetails_WithNegativeBasePrice_ThrowsDomainException()
    {
        var product = CreateProduct();

        Assert.Throws<DomainException>(() =>
            product.UpdateDetails("Product A", null, null, -1m, true, true, 0, Now));
    }
}
