using Knight.Domain.Exceptions;
using Subscriptions.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class SubscriptionAndEntitlementTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();

    private static Subscription Start(bool asTrial = false) =>
        Subscription.Start(Guid.NewGuid(), Now, CustomerId, PlanId, Now, Now.AddDays(30), asTrial);

    [Fact]
    public void ANewSubscriptionEntitlesImmediately()
    {
        var subscription = Start();

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.True(subscription.IsEntitling);
    }

    [Fact]
    public void ATrialEntitlesToo()
    {
        var subscription = Start(asTrial: true);

        Assert.Equal(SubscriptionStatus.Trial, subscription.Status);
        Assert.True(subscription.IsEntitling);
    }

    [Fact]
    public void PastDueStillEntitles_ButSuspensionDoesNot()
    {
        var subscription = Start();

        subscription.MarkPastDue(Now);
        Assert.True(subscription.IsEntitling);

        subscription.Suspend(Now);
        Assert.False(subscription.IsEntitling);
    }

    [Fact]
    public void CancellationIsTerminal()
    {
        var subscription = Start();
        subscription.Cancel(Now);

        Assert.False(subscription.IsEntitling);
        Assert.Equal(Now, subscription.CancelledAt);
        Assert.Throws<DomainException>(() => subscription.Activate(Now));
        Assert.Throws<DomainException>(() => subscription.Cancel(Now));
    }

    [Fact]
    public void ASuspendedSubscriptionCanBeBroughtBack()
    {
        var subscription = Start();
        subscription.Suspend(Now);

        subscription.Activate(Now);

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.True(subscription.IsEntitling);
    }

    [Fact]
    public void ChangingPlanClearsTheFeatureSelection()
    {
        var subscription = Start();
        subscription.EnableFeature(Guid.NewGuid(), null, Now);

        subscription.ChangePlan(Guid.NewGuid(), Now);

        // What was selectable on the old plan may be included, unavailable or
        // priced differently on the new one; carrying it over would silently
        // change what the customer pays for.
        Assert.Empty(subscription.Features);
    }

    [Fact]
    public void ChangingToTheSamePlanIsRefused()
    {
        var subscription = Start();

        Assert.Throws<DomainException>(() => subscription.ChangePlan(PlanId, Now));
    }

    [Fact]
    public void ACancelledSubscriptionCannotBeChanged()
    {
        var subscription = Start();
        subscription.Cancel(Now);

        Assert.Throws<DomainException>(() => subscription.ChangePlan(Guid.NewGuid(), Now));
        Assert.Throws<DomainException>(() => subscription.EnableFeature(Guid.NewGuid(), null, Now));
    }

    [Fact]
    public void DisablingKeepsTheRowSoTheHistorySurvives()
    {
        var subscription = Start();
        var featureId = Guid.NewGuid();

        subscription.EnableFeature(featureId, null, Now);
        subscription.DisableFeature(featureId, Now.AddDays(1));

        Assert.Single(subscription.Features);
        Assert.False(subscription.HasFeatureEnabled(featureId));
        Assert.Empty(subscription.EnabledFeatureIds);
    }

    [Fact]
    public void ReEnablingAPreviouslyDisabledFeatureReusesTheRow()
    {
        var subscription = Start();
        var featureId = Guid.NewGuid();

        subscription.EnableFeature(featureId, null, Now);
        subscription.DisableFeature(featureId, Now.AddDays(1));
        subscription.EnableFeature(featureId, null, Now.AddDays(2));

        Assert.Single(subscription.Features);
        Assert.True(subscription.HasFeatureEnabled(featureId));
    }

    [Fact]
    public void AdvancingThePeriodMovesTheWindowForward()
    {
        var subscription = Start();
        var previousEnd = subscription.CurrentPeriodEnd;

        subscription.AdvancePeriod(previousEnd.AddDays(30), Now);

        Assert.Equal(previousEnd, subscription.CurrentPeriodStart);
        Assert.Equal(previousEnd.AddDays(30), subscription.CurrentPeriodEnd);
    }

    [Fact]
    public void ThePeriodCannotMoveBackwards()
    {
        var subscription = Start();

        Assert.Throws<DomainException>(() => subscription.AdvancePeriod(subscription.CurrentPeriodEnd, Now));
    }

    // --- Entitlements ----------------------------------------------------

    private static FeatureEntitlement GrantPlan(Guid featureId) =>
        FeatureEntitlement.Grant(Guid.NewGuid(), CustomerId, featureId, EntitlementSource.Plan, Now);

    [Fact]
    public void AGrantIsActiveFromTheMomentItIsMade()
    {
        var entitlement = GrantPlan(Guid.NewGuid());

        Assert.False(entitlement.IsActiveAt(Now.AddSeconds(-1)));
        Assert.True(entitlement.IsActiveAt(Now));
    }

    [Fact]
    public void ATimeBoxedGrantLapsesOnItsOwn()
    {
        var entitlement = FeatureEntitlement.Grant(
            Guid.NewGuid(),
            CustomerId,
            Guid.NewGuid(),
            EntitlementSource.Grant,
            Now,
            Now.AddDays(14),
            grantedBy: Guid.NewGuid());

        Assert.True(entitlement.IsActiveAt(Now.AddDays(13)));
        Assert.False(entitlement.IsActiveAt(Now.AddDays(14)));
    }

    [Fact]
    public void AManualGrantMustRecordWhoMadeIt()
    {
        var exception = Assert.Throws<DomainException>(() => FeatureEntitlement.Grant(
            Guid.NewGuid(),
            CustomerId,
            Guid.NewGuid(),
            EntitlementSource.Grant,
            Now));

        Assert.Equal(DomainErrorCategory.Validation, exception.Category);
    }

    [Fact]
    public void RevokingRequiresAReasonAndIsIdempotent()
    {
        var entitlement = GrantPlan(Guid.NewGuid());

        Assert.Throws<DomainException>(() => entitlement.Revoke(Now, "  "));

        entitlement.Revoke(Now, "subscription_no_longer_grants");
        entitlement.Revoke(Now.AddDays(1), "something_else");

        Assert.False(entitlement.IsActiveAt(Now));
        Assert.Equal("subscription_no_longer_grants", entitlement.RevokedReason);
    }

    [Fact]
    public void AnExtensionMustMoveTheExpiryLater()
    {
        var entitlement = FeatureEntitlement.Grant(
            Guid.NewGuid(),
            CustomerId,
            Guid.NewGuid(),
            EntitlementSource.Grant,
            Now,
            Now.AddDays(14),
            grantedBy: Guid.NewGuid());

        Assert.Throws<DomainException>(() => entitlement.ExtendTo(Now.AddDays(7)));

        entitlement.ExtendTo(Now.AddDays(30));
        Assert.True(entitlement.IsActiveAt(Now.AddDays(20)));
    }

    [Fact]
    public void ARevokedEntitlementCannotBeExtended()
    {
        var entitlement = GrantPlan(Guid.NewGuid());
        entitlement.Revoke(Now, "revoked");

        Assert.Throws<DomainException>(() => entitlement.ExtendTo(Now.AddDays(30)));
    }

    [Fact]
    public void AnEntitlementCannotExpireBeforeItIsGranted()
    {
        Assert.Throws<DomainException>(() => FeatureEntitlement.Grant(
            Guid.NewGuid(),
            CustomerId,
            Guid.NewGuid(),
            EntitlementSource.Plan,
            Now,
            Now.AddDays(-1)));
    }
}
