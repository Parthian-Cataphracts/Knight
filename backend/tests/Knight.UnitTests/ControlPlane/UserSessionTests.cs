using AccessControl.Domain;
using Knight.Domain.Exceptions;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class UserSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ControlPlaneUser User() =>
        ControlPlaneUser.CreatePlatformStaff(Guid.NewGuid(), Now, "ops@knight.dev", "Ops", "hash");

    private static UserSession Start(bool mfaSatisfied = true) =>
        UserSession.StartFamily(Guid.NewGuid(), User(), "hash-1", Now, TimeSpan.FromHours(12), mfaSatisfied);

    [Fact]
    public void NewSession_IsActiveUntilItExpires()
    {
        var session = Start();

        Assert.True(session.IsActive(Now));
        Assert.True(session.IsActive(Now.AddHours(11)));
        Assert.False(session.IsActive(Now.AddHours(13)));
    }

    [Fact]
    public void Rotation_KeepsTheFamilyAndItsAbsoluteExpiry()
    {
        var session = Start();

        var replacement = session.Rotate(Guid.NewGuid(), "hash-2", Now.AddHours(1));

        Assert.Equal(session.FamilyId, replacement.FamilyId);
        Assert.Equal(session.ExpiresAt, replacement.ExpiresAt);
    }

    [Fact]
    public void Rotation_ConsumesThePresentedToken()
    {
        var session = Start();

        var replacement = session.Rotate(Guid.NewGuid(), "hash-2", Now.AddHours(1));

        Assert.False(session.IsActive(Now.AddHours(1)));
        Assert.Equal(replacement.Id, session.ReplacedBySessionId);
        Assert.True(replacement.IsActive(Now.AddHours(1)));
    }

    [Fact]
    public void AConsumedTokenPresentedAgain_ReadsAsAReplay()
    {
        var session = Start();
        session.Rotate(Guid.NewGuid(), "hash-2", Now.AddHours(1));

        Assert.True(session.IsReplay(Now.AddHours(2)));
    }

    [Fact]
    public void ARevokedTokenIsNotAReplay_ItIsSimplyGone()
    {
        var session = Start();
        session.Rotate(Guid.NewGuid(), "hash-2", Now.AddHours(1));
        session.Revoke(Now.AddHours(1), "refresh_token_reuse");

        Assert.False(session.IsReplay(Now.AddHours(2)));
    }

    [Fact]
    public void RotatingAConsumedSession_Throws()
    {
        var session = Start();
        session.Rotate(Guid.NewGuid(), "hash-2", Now.AddHours(1));

        Assert.Throws<DomainException>(() => session.Rotate(Guid.NewGuid(), "hash-3", Now.AddHours(2)));
    }

    [Fact]
    public void RotatingARevokedSession_Throws()
    {
        var session = Start();
        session.Revoke(Now, "logout");

        Assert.Throws<DomainException>(() => session.Rotate(Guid.NewGuid(), "hash-2", Now.AddHours(1)));
    }

    [Fact]
    public void RevokingTwice_IsHarmlessAndKeepsTheFirstReason()
    {
        var session = Start();
        session.Revoke(Now, "logout");
        session.Revoke(Now.AddMinutes(1), "something_else");

        Assert.Equal("logout", session.RevokedReason);
    }

    [Fact]
    public void RotationCarriesTheSecondFactorState()
    {
        var pending = Start(mfaSatisfied: false);

        var replacement = pending.Rotate(Guid.NewGuid(), "hash-2", Now.AddMinutes(5));

        Assert.False(replacement.MfaSatisfied);

        pending.MarkMfaSatisfied();
        Assert.True(pending.MfaSatisfied);
    }

    [Fact]
    public void ASessionIsBoundToItsAccountsCustomer()
    {
        var customerId = Guid.NewGuid();
        var user = ControlPlaneUser.CreateCustomerUser(Guid.NewGuid(), Now, customerId, "owner@cafe1.ir", "Owner", "hash");

        var session = UserSession.StartFamily(Guid.NewGuid(), user, "hash-1", Now, TimeSpan.FromHours(1), true);

        Assert.Equal(customerId, session.CustomerId);
    }
}
