using Identity.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.Identity;

public sealed class TenantUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Create_WithEmptyTenantId_ThrowsDomainException()
    {
        Assert.Throws<DomainException>(() =>
            TenantUser.Create(Guid.NewGuid(), Now, Guid.Empty, "user@example.com", "hash", "User"));
    }

    [Fact]
    public void Unlock_ClearsLockoutStateWithoutTouchingLastLoginAt()
    {
        var user = TenantUser.Create(Guid.NewGuid(), Now, Guid.NewGuid(), "user@example.com", "hash", "User");
        user.Activate(Now);
        for (var i = 0; i < 5; i++)
        {
            user.RegisterFailedLogin(Now, lockoutThreshold: 5, TimeSpan.FromMinutes(15));
        }

        Assert.True(user.IsLocked(Now));

        user.Unlock(Now.AddMinutes(1));

        Assert.False(user.IsLocked(Now.AddMinutes(1)));
        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LastLoginAt);
    }
}
