using AccessControl.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class ControlPlaneUserTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.NewGuid();

    private static ControlPlaneUser PlatformUser(string email = "Ops@Knight.dev") =>
        ControlPlaneUser.CreatePlatformStaff(Guid.NewGuid(), Now, email, "Ops", "hash");

    private static ControlPlaneUser CustomerUser() =>
        ControlPlaneUser.CreateCustomerUser(Guid.NewGuid(), Now, CustomerId, "owner@cafe1.ir", "Owner", "hash");

    [Fact]
    public void PlatformStaff_HasNoCustomer()
    {
        var user = PlatformUser();

        Assert.Null(user.CustomerId);
        Assert.True(user.IsPlatformStaff);
    }

    [Fact]
    public void CustomerUser_WithoutCustomer_Throws()
    {
        var exception = Assert.Throws<DomainException>(() =>
            ControlPlaneUser.CreateCustomerUser(Guid.NewGuid(), Now, Guid.Empty, "a@b.ir", "A", "hash"));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }

    [Fact]
    public void Create_NormalizesTheEmailForComparisonButKeepsTheDisplayForm()
    {
        var user = PlatformUser("Ops@Knight.dev");

        Assert.Equal("Ops@Knight.dev", user.Email);
        Assert.Equal("OPS@KNIGHT.DEV", user.NormalizedEmail);
    }

    [Fact]
    public void Create_StartsInvitedAndCannotAuthenticate()
    {
        var user = PlatformUser();

        Assert.Equal(AccountStatus.Invited, user.Status);
        Assert.False(user.CanAuthenticate(Now));
    }

    [Fact]
    public void ActivatedAccount_CanAuthenticate()
    {
        var user = PlatformUser();
        user.Activate(Now);

        Assert.True(user.CanAuthenticate(Now));
    }

    [Fact]
    public void DisabledAccount_CannotBeReactivated()
    {
        var user = PlatformUser();
        user.Disable(Now);

        var exception = Assert.Throws<DomainException>(() => user.Activate(Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void FailedLogins_LockTheAccountAtTheThreshold()
    {
        var user = PlatformUser();
        user.Activate(Now);

        user.RegisterFailedLogin(Now, 3, TimeSpan.FromMinutes(15));
        user.RegisterFailedLogin(Now, 3, TimeSpan.FromMinutes(15));

        Assert.False(user.IsLocked(Now));

        user.RegisterFailedLogin(Now, 3, TimeSpan.FromMinutes(15));

        Assert.True(user.IsLocked(Now));
        Assert.False(user.CanAuthenticate(Now));
    }

    [Fact]
    public void Lockout_ExpiresOnItsOwn()
    {
        var user = PlatformUser();
        user.Activate(Now);
        user.RegisterFailedLogin(Now, 1, TimeSpan.FromMinutes(15));

        Assert.True(user.IsLocked(Now));
        Assert.False(user.IsLocked(Now.AddMinutes(16)));
    }

    [Fact]
    public void SuccessfulLogin_ClearsTheFailureCount()
    {
        var user = PlatformUser();
        user.Activate(Now);
        user.RegisterFailedLogin(Now, 5, TimeSpan.FromMinutes(15));

        user.RegisterSuccessfulLogin(Now);

        Assert.Equal(0, user.FailedLoginCount);
        Assert.Null(user.LockedUntil);
        Assert.Equal(Now, user.LastLoginAt);
    }

    [Fact]
    public void MfaIsNotInForceUntilTheOwnerHasProvedTheyCanProduceACode()
    {
        var user = PlatformUser();
        user.BeginMfaEnrollment("SECRET", Now);

        Assert.False(user.MfaEnabled);

        user.ConfirmMfa(Now);

        Assert.True(user.MfaEnabled);
        Assert.Equal(Now, user.MfaConfirmedAt);
    }

    [Fact]
    public void ConfirmingMfa_WithoutEnrolling_Throws()
    {
        var user = PlatformUser();

        Assert.Throws<DomainException>(() => user.ConfirmMfa(Now));
    }

    [Fact]
    public void DisablingMfa_ClearsTheSecret()
    {
        var user = PlatformUser();
        user.BeginMfaEnrollment("SECRET", Now);
        user.ConfirmMfa(Now);

        user.DisableMfa(Now);

        Assert.False(user.MfaEnabled);
        Assert.Null(user.MfaSecret);
    }

    [Fact]
    public void CustomerAccount_CannotHoldAPlatformRole()
    {
        var user = CustomerUser();
        var role = Role.CreateSystem(Guid.NewGuid(), Now, SystemRoles.Admin, RoleScope.Platform);

        var exception = Assert.Throws<DomainException>(() => user.AssignRole(Guid.NewGuid(), role, Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void PlatformAccount_CannotHoldACustomerRole()
    {
        var user = PlatformUser();
        var role = Role.CreateSystem(Guid.NewGuid(), Now, SystemRoles.CustomerOwner, RoleScope.Customer);

        Assert.Throws<DomainException>(() => user.AssignRole(Guid.NewGuid(), role, Now));
    }

    [Fact]
    public void AssignedRole_CarriesTheAccountsCustomer()
    {
        var user = CustomerUser();
        var role = Role.CreateSystem(Guid.NewGuid(), Now, SystemRoles.CustomerOwner, RoleScope.Customer);

        var assignment = user.AssignRole(Guid.NewGuid(), role, Now);

        Assert.Equal(CustomerId, assignment.CustomerId);
        Assert.Single(user.Roles);
    }

    [Fact]
    public void AssigningTheSameRoleTwice_Throws()
    {
        var user = CustomerUser();
        var role = Role.CreateSystem(Guid.NewGuid(), Now, SystemRoles.CustomerOwner, RoleScope.Customer);
        user.AssignRole(Guid.NewGuid(), role, Now);

        Assert.Throws<DomainException>(() => user.AssignRole(Guid.NewGuid(), role, Now));
    }

    [Fact]
    public void RemovingARoleTheAccountDoesNotHold_Throws()
    {
        var user = CustomerUser();

        Assert.Throws<DomainException>(() => user.RemoveRole(Guid.NewGuid(), Now));
    }
}
