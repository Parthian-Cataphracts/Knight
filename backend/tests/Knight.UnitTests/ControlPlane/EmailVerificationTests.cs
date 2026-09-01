using AccessControl.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The email-verification half of self-service sign-up
/// (docs/self-service-saas-plan.md §12, phase B). A self-service account holds
/// its password from the first moment but cannot sign in until it proves control
/// of its address, and the token behaves like every other one-shot secret here:
/// cleared on use, refused once expired.
/// </summary>
public sealed class EmailVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.NewGuid();

    private static ControlPlaneUser NewCustomerAccount() =>
        ControlPlaneUser.CreateCustomerUser(Guid.NewGuid(), Now, CustomerId, "merchant@example.test", "Merchant", "hash");

    [Fact]
    public void ANewlyRegisteredAccountIsUnverifiedAndCannotAuthenticate()
    {
        var user = NewCustomerAccount();
        user.BeginEmailVerification("token-hash", Now.AddHours(24), Now);

        Assert.False(user.EmailVerified);
        Assert.Equal(AccountStatus.Invited, user.Status);
        Assert.False(user.CanAuthenticate(Now.AddMinutes(1)));
    }

    [Fact]
    public void ConfirmingVerificationMakesItUsableWithoutTouchingThePassword()
    {
        var user = NewCustomerAccount();
        user.BeginEmailVerification("token-hash", Now.AddHours(24), Now);

        user.ConfirmEmailVerification(Now.AddMinutes(5));

        Assert.True(user.EmailVerified);
        Assert.Equal(AccountStatus.Active, user.Status);
        Assert.True(user.CanAuthenticate(Now.AddMinutes(6)));
        Assert.Equal("hash", user.PasswordHash);
    }

    [Fact]
    public void ConfirmingClearsTheTokenSoTheLinkCannotBeReplayed()
    {
        var user = NewCustomerAccount();
        user.BeginEmailVerification("token-hash", Now.AddHours(24), Now);
        user.ConfirmEmailVerification(Now.AddMinutes(5));

        Assert.Throws<DomainException>(() => user.ConfirmEmailVerification(Now.AddMinutes(6)));
    }

    [Fact]
    public void AnExpiredVerificationIsRefused()
    {
        var user = NewCustomerAccount();
        user.BeginEmailVerification("token-hash", Now.AddHours(24), Now);

        Assert.Throws<DomainException>(() => user.ConfirmEmailVerification(Now.AddHours(25)));
        Assert.False(user.EmailVerified);
    }

    [Fact]
    public void VerifyingWithoutAnOutstandingTokenIsRefused()
    {
        var user = NewCustomerAccount();

        Assert.Throws<DomainException>(() => user.ConfirmEmailVerification(Now));
    }
}
