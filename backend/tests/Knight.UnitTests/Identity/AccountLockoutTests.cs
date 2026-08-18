using Identity.Domain;
using Xunit;

namespace Knight.UnitTests.Identity;

public sealed class AccountLockoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static PlatformAdmin CreateActiveAdmin()
    {
        var admin = PlatformAdmin.Create(Guid.NewGuid(), Now, "admin@example.com", "hash", "Admin");
        admin.Activate(Now);
        return admin;
    }

    [Fact]
    public void RegisterFailedLogin_BelowThreshold_DoesNotLock()
    {
        var admin = CreateActiveAdmin();

        admin.RegisterFailedLogin(Now, lockoutThreshold: 5, TimeSpan.FromMinutes(15));

        Assert.False(admin.IsLocked(Now));
        Assert.Equal(1, admin.FailedLoginCount);
    }

    [Fact]
    public void RegisterFailedLogin_AtThreshold_Locks()
    {
        var admin = CreateActiveAdmin();

        for (var i = 0; i < 5; i++)
        {
            admin.RegisterFailedLogin(Now, lockoutThreshold: 5, TimeSpan.FromMinutes(15));
        }

        Assert.True(admin.IsLocked(Now));
        Assert.False(admin.CanAuthenticate(Now));
    }

    [Fact]
    public void IsLocked_AfterLockoutDurationElapses_ReturnsFalse()
    {
        var admin = CreateActiveAdmin();
        for (var i = 0; i < 5; i++)
        {
            admin.RegisterFailedLogin(Now, lockoutThreshold: 5, TimeSpan.FromMinutes(15));
        }

        Assert.True(admin.IsLocked(Now.AddMinutes(14)));
        Assert.False(admin.IsLocked(Now.AddMinutes(16)));
    }

    [Fact]
    public void RegisterSuccessfulLogin_ResetsFailedCountAndLock()
    {
        var admin = CreateActiveAdmin();
        for (var i = 0; i < 5; i++)
        {
            admin.RegisterFailedLogin(Now, lockoutThreshold: 5, TimeSpan.FromMinutes(15));
        }

        admin.RegisterSuccessfulLogin(Now.AddMinutes(20));

        Assert.Equal(0, admin.FailedLoginCount);
        Assert.False(admin.IsLocked(Now.AddMinutes(20)));
        Assert.True(admin.CanAuthenticate(Now.AddMinutes(20)));
    }

    [Fact]
    public void CanAuthenticate_WhenSuspended_ReturnsFalse()
    {
        var admin = PlatformAdmin.Create(Guid.NewGuid(), Now, "admin@example.com", "hash", "Admin");
        admin.Activate(Now);
        admin.Suspend(Now);

        Assert.False(admin.CanAuthenticate(Now));
    }

    [Fact]
    public void CanAuthenticate_WhenDisabled_ReturnsFalse()
    {
        var admin = PlatformAdmin.Create(Guid.NewGuid(), Now, "admin@example.com", "hash", "Admin");
        admin.Activate(Now);
        admin.Disable(Now);

        Assert.False(admin.CanAuthenticate(Now));
    }

    [Theory]
    [InlineData("Admin@Example.com")]
    [InlineData("  admin@example.com  ")]
    [InlineData("ADMIN@EXAMPLE.COM")]
    public void Create_NormalizesEmailForComparisonRegardlessOfCasingOrWhitespace(string rawEmail)
    {
        var admin = PlatformAdmin.Create(Guid.NewGuid(), Now, rawEmail, "hash", "Admin");

        Assert.Equal("ADMIN@EXAMPLE.COM", admin.NormalizedEmail);
    }
}
