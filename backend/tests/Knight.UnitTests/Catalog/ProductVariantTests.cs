using Catalog.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Catalog;

public sealed class ProductVariantTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ProductVariant CreateVariant(
        decimal price = 10m,
        decimal? compareAtPrice = null,
        string? sku = null,
        bool isDefault = false) =>
        ProductVariant.Create(
            Guid.NewGuid(),
            Now,
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Variant A",
            sku,
            price,
            compareAtPrice,
            isDefault,
            isAvailable: true,
            displayOrder: 0);

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => ProductVariant.Create(
            Guid.NewGuid(), Now, Guid.Empty, Guid.NewGuid(), "Variant A", null, 10m, null, false, true, 0));
    }

    [Fact]
    public void Create_WithEmptyProductId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => ProductVariant.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), Guid.Empty, "Variant A", null, 10m, null, false, true, 0));
    }

    [Fact]
    public void Create_WithNegativePrice_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => CreateVariant(-0.01m));
        Assert.Throws<DomainException>(() => CreateVariant(-1m));
    }

    [Fact]
    public void Create_WithNegativeCompareAtPrice_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() => CreateVariant(compareAtPrice: -0.01m));
        Assert.Throws<DomainException>(() => CreateVariant(compareAtPrice: -1m));
    }

    [Fact]
    public void Create_WithNullSku_LeavesBothSkuColumnsNull()
    {
        var variant = CreateVariant();

        Assert.Null(variant.Sku);
        Assert.Null(variant.NormalizedSku);
    }

    [Fact]
    public void Create_WithBlankSku_LeavesBothSkuColumnsNull()
    {
        var variant = CreateVariant(sku: "   ");

        Assert.Null(variant.Sku);
        Assert.Null(variant.NormalizedSku);
    }

    [Fact]
    public void Create_NormalizesSkuToUppercaseWhilePreservingTheDisplayForm()
    {
        var variant = CreateVariant(sku: "  sku-a1  ");

        Assert.Equal("sku-a1", variant.Sku);
        Assert.Equal("SKU-A1", variant.NormalizedSku);
    }

    [Fact]
    public void Create_WithSkuExceedingMaxLength_ThrowsDomainException()
    {
        var tooLong = new string('a', 101);

        Assert.Throws<DomainException>(() => CreateVariant(sku: tooLong));
    }

    [Fact]
    public void MarkAsDefault_SetsTheFlagAndTimestamp()
    {
        var variant = CreateVariant();

        variant.MarkAsDefault(Now.AddMinutes(1));

        Assert.True(variant.IsDefault);
        Assert.Equal(Now.AddMinutes(1), variant.UpdatedAt);
    }

    [Fact]
    public void ClearDefault_ClearsTheFlag()
    {
        var variant = CreateVariant(isDefault: true);

        variant.ClearDefault(Now.AddMinutes(1));

        Assert.False(variant.IsDefault);
    }

    [Fact]
    public void MarkAsDefaultThenClearDefault_TogglesBackAndForth()
    {
        var variant = CreateVariant();

        variant.MarkAsDefault(Now.AddMinutes(1));
        variant.ClearDefault(Now.AddMinutes(2));
        variant.MarkAsDefault(Now.AddMinutes(3));

        Assert.True(variant.IsDefault);
    }

    [Fact]
    public void ChangePrice_WithNegativeValue_ThrowsDomainException()
    {
        var variant = CreateVariant();

        Assert.Throws<DomainException>(() => variant.ChangePrice(-1m, Now));
    }

    [Fact]
    public void UpdateDetails_WithNegativePrice_ThrowsDomainException()
    {
        var variant = CreateVariant();

        Assert.Throws<DomainException>(() =>
            variant.UpdateDetails("Variant A", null, -1m, null, true, 0, Now));
    }

    [Fact]
    public void UpdateDetails_ClearingTheSku_ClearsTheNormalizedForm()
    {
        var variant = CreateVariant(sku: "SKU-A1");

        variant.UpdateDetails("Variant A", null, 12m, null, true, 0, Now.AddMinutes(1));

        Assert.Null(variant.Sku);
        Assert.Null(variant.NormalizedSku);
        Assert.Equal(12m, variant.Price);
    }
}
