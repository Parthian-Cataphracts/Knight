using Knight.Application.Authorization;
using Xunit;

namespace Knight.UnitTests.Authorization;

public sealed class PermissionTests
{
    [Theory]
    [InlineData("catalog.products.view")]
    [InlineData("orders.cancel")]
    public void Constructor_WithValidKey_Succeeds(string key)
    {
        var permission = new Permission(key);

        Assert.Equal(key, permission.Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("NoDots")]
    [InlineData("Has.Uppercase")]
    [InlineData(".leadingdot")]
    public void Constructor_WithInvalidKey_ThrowsArgumentException(string key)
    {
        Assert.Throws<ArgumentException>(() => new Permission(key));
    }

    [Fact]
    public void PermissionCatalog_Register_IsIdempotentForDuplicateKeys()
    {
        var catalog = new PermissionCatalog();
        var permission = new Permission("catalog.products.view");

        catalog.Register([permission, permission]);

        Assert.Single(catalog.All);
    }
}
