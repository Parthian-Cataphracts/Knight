using Knight.Infrastructure.Security;
using Xunit;

namespace Knight.UnitTests.Security;

public sealed class PasswordHasherTests
{
    [Fact]
    public void Verify_WithCorrectPassword_ReturnsTrue()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");

        Assert.True(hasher.Verify("correct horse battery staple", hash));
    }

    [Fact]
    public void Verify_WithIncorrectPassword_ReturnsFalse()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var hash = hasher.Hash("correct horse battery staple");

        Assert.False(hasher.Verify("wrong password", hash));
    }

    [Fact]
    public void Hash_ProducesDifferentOutputForSamePasswordDueToRandomSalt()
    {
        var hasher = new Pbkdf2PasswordHasher();

        var first = hasher.Hash("same-password");
        var second = hasher.Hash("same-password");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NeedsRehash_ForCurrentHash_ReturnsFalse()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var hash = hasher.Hash("some-password");

        Assert.False(hasher.NeedsRehash(hash));
    }

    [Fact]
    public void NeedsRehash_ForOutdatedIterationCount_ReturnsTrue()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var hash = hasher.Hash("some-password");
        var outdated = "1000" + hash[hash.IndexOf('.')..];

        Assert.True(hasher.NeedsRehash(outdated));
    }

    [Fact]
    public void NeedsRehash_ForMalformedHash_ReturnsTrue()
    {
        var hasher = new Pbkdf2PasswordHasher();

        Assert.True(hasher.NeedsRehash("not-a-valid-hash"));
    }
}
