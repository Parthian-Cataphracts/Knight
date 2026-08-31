using Knight.Domain.Common;
using Knight.Domain.Exceptions;
using PlatformBilling.Domain;
using Plans.Domain;
using Provisioning.Domain;
using Subscriptions.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// Phase A of the self-service SaaS track (docs/self-service-saas-plan.md §12):
/// the domain foundations that later phases build the public flow on. These pin
/// the transitions a browser redirect must never be able to fake — a pending
/// subscription entitles nothing until a verified payment activates it — and the
/// separate platform-billing aggregates.
/// </summary>
public sealed class SelfServiceSaaSFoundationsTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();
    private static readonly Guid SubscriptionId = Guid.NewGuid();

    private static Money Usd(decimal amount) => Money.Of(amount, "USD");

    // --- Subscription: the pending → active gate ------------------------------

    [Fact]
    public void APendingSubscriptionEntitlesToNothingUntilActivated()
    {
        var subscription = Subscription.StartPending(Guid.NewGuid(), Now, CustomerId, PlanId, Now, Now.AddDays(30));

        Assert.Equal(SubscriptionStatus.Pending, subscription.Status);
        Assert.False(subscription.IsEntitling);
    }

    [Fact]
    public void ActivatingAPendingSubscriptionEntitlesIt()
    {
        var subscription = Subscription.StartPending(Guid.NewGuid(), Now, CustomerId, PlanId, Now, Now.AddDays(30));

        subscription.Activate(Now.AddMinutes(1));

        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.True(subscription.IsEntitling);
    }

    [Fact]
    public void APendingSubscriptionMayHaveOptionalFeaturesChosenBeforePayment()
    {
        var featureId = Guid.NewGuid();
        var subscription = Subscription.StartPending(Guid.NewGuid(), Now, CustomerId, PlanId, Now, Now.AddDays(30));

        subscription.EnableFeature(featureId, enabledBy: null, Now);

        Assert.Contains(featureId, subscription.EnabledFeatureIds);
    }

    [Fact]
    public void LinkingAProviderRecordsHowTheWebhookWillFindTheSubscription()
    {
        var subscription = Subscription.StartPending(Guid.NewGuid(), Now, CustomerId, PlanId, Now, Now.AddDays(30));

        subscription.LinkProvider("sandbox", "sub_ABC123", Now);

        Assert.Equal("sandbox", subscription.Provider);
        Assert.Equal("sub_ABC123", subscription.ProviderSubscriptionId);
    }

    [Fact]
    public void CancellingAtPeriodEndDoesNotEndTheSubscriptionNow()
    {
        var subscription = Subscription.Start(Guid.NewGuid(), Now, CustomerId, PlanId, Now, Now.AddDays(30));

        subscription.RequestCancelAtPeriodEnd(Now.AddDays(1));

        Assert.True(subscription.CancelAtPeriodEnd);
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
    }

    [Fact]
    public void ACancelledSubscriptionCannotBeActivated()
    {
        var subscription = Subscription.StartPending(Guid.NewGuid(), Now, CustomerId, PlanId, Now, Now.AddDays(30));
        subscription.Cancel(Now.AddMinutes(1));

        Assert.Throws<DomainException>(() => subscription.Activate(Now.AddMinutes(2)));
    }

    // --- Plan: public purchasability ------------------------------------------

    [Fact]
    public void APlanIsNotPubliclyPurchasableUntilPutOnTheList()
    {
        var plan = Plan.Create(Guid.NewGuid(), Now, "basic", "Basic", Usd(29));

        Assert.False(plan.IsPubliclyPurchasable);

        plan.SetPubliclyPurchasable(true, Now.AddMinutes(1));
        Assert.True(plan.IsPubliclyPurchasable);

        plan.SetPubliclyPurchasable(false, Now.AddMinutes(2));
        Assert.False(plan.IsPubliclyPurchasable);
    }

    // --- PlatformBillingTransaction -------------------------------------------

    private static PlatformBillingTransaction RecordCharge(string idempotencyKey = "evt_1") =>
        PlatformBillingTransaction.Record(Guid.NewGuid(), Now, CustomerId, SubscriptionId, "sandbox", Usd(79), idempotencyKey);

    [Fact]
    public void AChargeStartsPendingAndSucceedsOnce()
    {
        var charge = RecordCharge();
        Assert.Equal(PlatformBillingTransactionStatus.Pending, charge.Status);

        charge.Succeed(Now.AddMinutes(1));
        Assert.Equal(PlatformBillingTransactionStatus.Succeeded, charge.Status);

        Assert.Throws<DomainException>(() => charge.Succeed(Now.AddMinutes(2)));
    }

    [Fact]
    public void AFailedChargeCannotThenSucceed()
    {
        var charge = RecordCharge();
        charge.Fail(Now.AddMinutes(1));

        Assert.Equal(PlatformBillingTransactionStatus.Failed, charge.Status);
        Assert.Throws<DomainException>(() => charge.Succeed(Now.AddMinutes(2)));
    }

    [Fact]
    public void AChargeRequiresAnIdempotencyKey()
    {
        Assert.Throws<DomainException>(() => RecordCharge(idempotencyKey: "   "));
    }

    [Fact]
    public void AFullRefundLeavesItRefunded()
    {
        var charge = RecordCharge();
        charge.Succeed(Now.AddMinutes(1));

        charge.Refund(Usd(79), Now.AddMinutes(2));

        Assert.Equal(PlatformBillingTransactionStatus.Refunded, charge.Status);
    }

    [Fact]
    public void PartialRefundsAddUpAndCannotExceedTheCharge()
    {
        var charge = RecordCharge();
        charge.Succeed(Now.AddMinutes(1));

        charge.Refund(Usd(30), Now.AddMinutes(2));
        Assert.Equal(PlatformBillingTransactionStatus.PartiallyRefunded, charge.Status);

        Assert.Throws<DomainException>(() => charge.Refund(Usd(60), Now.AddMinutes(3)));

        charge.Refund(Usd(49), Now.AddMinutes(4));
        Assert.Equal(PlatformBillingTransactionStatus.Refunded, charge.Status);
    }

    // --- CheckoutSession ------------------------------------------------------

    private static CheckoutSession OpenCheckout(IEnumerable<Guid>? features = null) =>
        CheckoutSession.Open(
            Guid.NewGuid(), Now, CustomerId, PlanId, SubscriptionId,
            BillingInterval.Monthly, features ?? [], Usd(79), Now.AddHours(1));

    [Fact]
    public void ACheckoutOpensAndCompletesOnce()
    {
        var checkout = OpenCheckout();
        Assert.Equal(CheckoutSessionStatus.Open, checkout.Status);

        checkout.AttachProviderSession("sandbox", "cs_123", Now.AddMinutes(1));
        checkout.Complete(Now.AddMinutes(2));

        Assert.Equal(CheckoutSessionStatus.Completed, checkout.Status);
        Assert.Throws<DomainException>(() => checkout.Complete(Now.AddMinutes(3)));
    }

    [Fact]
    public void CheckoutStoresTheAuthoritativeTotalAndDeduplicatesFeatures()
    {
        var feature = Guid.NewGuid();
        var checkout = OpenCheckout([feature, feature]);

        Assert.Equal(79m, checkout.Total.Amount);
        Assert.Equal("USD", checkout.Total.Currency);
        Assert.Single(checkout.SelectedFeatureIds);
    }

    [Fact]
    public void ExpiringAnExpiredCheckoutIsANoOp()
    {
        var checkout = OpenCheckout();
        checkout.Expire(Now.AddMinutes(1));

        var exception = Record.Exception(() => checkout.Expire(Now.AddMinutes(2)));

        Assert.Null(exception);
        Assert.Equal(CheckoutSessionStatus.Expired, checkout.Status);
    }

    // --- ProvisioningJob: failure classification and attempts -----------------

    private static ProvisioningJob StartJob() =>
        ProvisioningJob.Start(
            Guid.NewGuid(), Now, CustomerId, Guid.NewGuid(),
            ProvisioningKind.Provision, "idem-1", "corr-1", Guid.NewGuid());

    [Fact]
    public void AJobStartsOnItsFirstAttempt()
    {
        Assert.Equal(1, StartJob().AttemptCount);
    }

    [Fact]
    public void FailingRecordsHowToTreatItAndRetryingCountsAnotherAttempt()
    {
        var job = StartJob();

        job.Fail("infra.timeout", "The infrastructure API timed out.", Now.AddMinutes(1), ProvisioningFailureClass.Transient);
        Assert.Equal(ProvisioningState.Failed, job.State);
        Assert.Equal(ProvisioningFailureClass.Transient, job.FailureClass);

        job.Retry(Now.AddMinutes(2));
        Assert.Equal(ProvisioningState.Running, job.State);
        Assert.Null(job.FailureClass);
        Assert.Equal(2, job.AttemptCount);
    }

    [Fact]
    public void APermanentFailureIsRecordedAsSuch()
    {
        var job = StartJob();

        job.Fail("config.invalid", "The provisioning configuration is invalid.", Now.AddMinutes(1), ProvisioningFailureClass.Permanent);

        Assert.Equal(ProvisioningFailureClass.Permanent, job.FailureClass);
    }
}
