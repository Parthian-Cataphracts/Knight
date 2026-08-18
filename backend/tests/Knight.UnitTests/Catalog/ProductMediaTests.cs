using Catalog.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Catalog;

public sealed class ProductMediaTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ProductMedia CreateMedia(string storageKey) =>
        ProductMedia.Create(Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), storageKey, "Alt text", 0, isPrimary: false);

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            ProductMedia.Create(Guid.NewGuid(), Now, Guid.Empty, Guid.NewGuid(), "products/abc123/photo.jpg", null, 0, false));
    }

    [Fact]
    public void Create_WithEmptyProductId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            ProductMedia.Create(Guid.NewGuid(), Now, Guid.NewGuid(), Guid.Empty, "products/abc123/photo.jpg", null, 0, false));
    }

    [Theory]
    [InlineData("products/abc123/photo.jpg")]
    [InlineData("products/abc123/thumbnails/small.webp")]
    [InlineData("photo.jpg")]
    public void Create_WithLogicalObjectKey_Succeeds(string storageKey)
    {
        var media = CreateMedia(storageKey);

        Assert.Equal(storageKey, media.StorageKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WithBlankStorageKey_ThrowsDomainException(string storageKey)
    {
        Assert.Throws<DomainException>(() => CreateMedia(storageKey));
    }

    /// <summary>
    /// A storage key must never be reinterpretable as a filesystem path by any
    /// downstream consumer, so traversal sequences, absolute POSIX/UNC paths and
    /// Windows drive-qualified paths are all rejected at construction.
    /// </summary>
    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("products/../../etc/passwd")]
    [InlineData("products/..")]
    [InlineData("/etc/passwd")]
    [InlineData("/products/abc123/photo.jpg")]
    [InlineData("\\\\server\\share\\photo.jpg")]
    [InlineData("C:\\products\\photo.jpg")]
    [InlineData("D:/products/photo.jpg")]
    public void Create_WithFilesystemLikeStorageKey_ThrowsDomainException(string storageKey)
    {
        Assert.Throws<DomainException>(() => CreateMedia(storageKey));
    }

    [Fact]
    public void Create_WithStorageKeyExceedingMaxLength_ThrowsDomainException()
    {
        var tooLong = new string('a', 501);

        Assert.Throws<DomainException>(() => CreateMedia(tooLong));
    }

    [Fact]
    public void Create_WithAltTextExceedingMaxLength_ThrowsDomainException()
    {
        var tooLong = new string('a', 301);

        Assert.Throws<DomainException>(() => ProductMedia.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), "products/abc123/photo.jpg", tooLong, 0, false));
    }

    [Fact]
    public void Create_WithBlankAltText_StoresNull()
    {
        var media = ProductMedia.Create(
            Guid.NewGuid(), Now, Guid.NewGuid(), Guid.NewGuid(), "products/abc123/photo.jpg", "   ", 0, false);

        Assert.Null(media.AltText);
    }

    [Fact]
    public void SetPrimary_TogglesTheFlag()
    {
        var media = CreateMedia("products/abc123/photo.jpg");

        media.SetPrimary(true);
        Assert.True(media.IsPrimary);

        media.SetPrimary(false);
        Assert.False(media.IsPrimary);
    }
}
