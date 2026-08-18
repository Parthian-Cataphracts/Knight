using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using NSubstitute;
using Subscriptions;
using Subscriptions.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The rules that decide what a customer is owed. These are the ones an
/// integration test would only reach through several endpoints, and the ones that
/// cost money when they are wrong.
/// </summary>
public sealed class EntitlementServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly Guid PlanId = Guid.NewGuid();

    private readonly IFeatureEntitlementRepository _entitlements = Substitute.For<IFeatureEntitlementRepository>();
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly IPlanCatalogReader _plans = Substitute.For<IPlanCatalogReader>();
    private readonly IFeatureCatalogReader _features = Substitute.For<IFeatureCatalogReader>();
    private readonly IStoreHostingReader _hosting = Substitute.For<IStoreHostingReader>();
    private readonly IEntitlementEventPublisher _events = Substitute.For<IEntitlementEventPublisher>();
    private readonly IAuditTrail _audit = Substitute.For<IAuditTrail>();
    private readonly IDateTimeProvider _clock = Substitute.For<IDateTimeProvider>();

    private readonly IEntitlementService _service;

    public EntitlementServiceTests()
    {
        _clock.UtcNow.Returns(Now);
        _entitlements.ListForCustomerAsync(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns([]);
        _hosting.HasDedicatedCapacityAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);

        _service = new EntitlementService(
            _entitlements,
            _subscriptions,
            _plans,
            _features,
            _hosting,
            _events,
            _audit,
            _clock);
    }

    private static FeatureDescriptor Descriptor(
        Guid featureId,
        bool dedicated = false,
        string status = "Published",
        bool canBeEntitled = true,
        bool remainsEntitled = true) =>
        new(featureId, "analytics", status, true, dedicated, canBeEntitled, remainsEntitled);

    private void FeatureIs(Guid featureId, FeatureDescriptor descriptor)
    {
        _features.GetAsync(featureId, Arg.Any<CancellationToken>()).Returns(descriptor);
        _features.GetManyAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns([descriptor]);
    }

    private void PlanOffers(Guid featureId, bool isIncluded, bool isCustomerToggleable)
        => _plans.GetOfferingAsync(PlanId, Arg.Any<CancellationToken>()).Returns(
            new PlanOffering(PlanId, "basic", "Basic", [new PlanFeatureOffering(featureId, isIncluded, isCustomerToggleable, null)]));

    private Subscription SubscriptionExists(bool entitling = true)
    {
        var subscription = Subscription.Start(Guid.NewGuid(), Now, CustomerId, PlanId, Now, Now.AddDays(30));

        if (!entitling)
        {
            subscription.Suspend(Now);
        }

        _subscriptions.GetActiveForCustomerAsync(CustomerId, Arg.Any<CancellationToken>()).Returns(subscription);
        return subscription;
    }

    [Fact]
    public async Task AFeatureTheCustomerMayChooseIsAllowed()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId));
        PlanOffers(featureId, isIncluded: false, isCustomerToggleable: true);
        SubscriptionExists();

        var decision = await _service.CanEntitleAsync(CustomerId, featureId, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task AFeatureThePlanDoesNotLetTheCustomerToggleIsRefused()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId));
        PlanOffers(featureId, isIncluded: false, isCustomerToggleable: false);
        SubscriptionExists();

        var decision = await _service.CanEntitleAsync(CustomerId, featureId, CancellationToken.None);

        Assert.Equal(EntitlementRefusal.NotOfferedByPlan, decision.Refusal);
    }

    [Fact]
    public async Task AFeatureThePlanDoesNotListAtAllIsRefused()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId));
        _plans.GetOfferingAsync(PlanId, Arg.Any<CancellationToken>())
            .Returns(new PlanOffering(PlanId, "basic", "Basic", []));
        SubscriptionExists();

        var decision = await _service.CanEntitleAsync(CustomerId, featureId, CancellationToken.None);

        Assert.Equal(EntitlementRefusal.NotOfferedByPlan, decision.Refusal);
    }

    [Fact]
    public async Task ADedicatedInfrastructureFeatureIsRefusedOnSharedHosting()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId, dedicated: true));
        PlanOffers(featureId, isIncluded: false, isCustomerToggleable: true);
        SubscriptionExists();

        var decision = await _service.CanEntitleAsync(CustomerId, featureId, CancellationToken.None);

        Assert.Equal(EntitlementRefusal.RequiresDedicatedInfrastructure, decision.Refusal);
    }

    [Fact]
    public async Task ADedicatedInfrastructureFeatureIsAllowedWithDedicatedCapacity()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId, dedicated: true));
        PlanOffers(featureId, isIncluded: false, isCustomerToggleable: true);
        SubscriptionExists();
        _hosting.HasDedicatedCapacityAsync(CustomerId, Arg.Any<CancellationToken>()).Returns(true);

        var decision = await _service.CanEntitleAsync(CustomerId, featureId, CancellationToken.None);

        Assert.True(decision.IsAllowed);
    }

    [Fact]
    public async Task ADraftFeatureCannotBeEntitled()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId, status: "Draft", canBeEntitled: false, remainsEntitled: false));
        SubscriptionExists();

        var decision = await _service.CanEntitleAsync(CustomerId, featureId, CancellationToken.None);

        Assert.Equal(EntitlementRefusal.FeatureNotAvailable, decision.Refusal);
    }

    [Fact]
    public async Task ASuspendedSubscriptionEntitlesNothing()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId));
        PlanOffers(featureId, isIncluded: true, isCustomerToggleable: false);
        SubscriptionExists(entitling: false);

        var decision = await _service.CanEntitleAsync(CustomerId, featureId, CancellationToken.None);

        Assert.Equal(EntitlementRefusal.NoEntitlingSubscription, decision.Refusal);
    }

    [Fact]
    public async Task ReconciliationGrantsWhatThePlanIncludes()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId));
        PlanOffers(featureId, isIncluded: true, isCustomerToggleable: false);
        SubscriptionExists();

        await _service.ReconcileAsync(CustomerId, CancellationToken.None);

        await _entitlements.Received(1).AddAsync(
            Arg.Is<FeatureEntitlement>(e => e.FeatureId == featureId && e.Source == EntitlementSource.Plan),
            Arg.Any<CancellationToken>());

        await _events.Received(1).PublishAsync(
            Arg.Is<FeatureEntitlementGranted>(e => e.FeatureId == featureId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconciliationRevokesWhatTheSubscriptionNoLongerGrants()
    {
        var featureId = Guid.NewGuid();
        var stale = FeatureEntitlement.Grant(Guid.NewGuid(), CustomerId, featureId, EntitlementSource.Plan, Now.AddDays(-10));

        _entitlements.ListForCustomerAsync(CustomerId, false, Arg.Any<CancellationToken>()).Returns([stale]);
        _plans.GetOfferingAsync(PlanId, Arg.Any<CancellationToken>()).Returns(new PlanOffering(PlanId, "basic", "Basic", []));
        SubscriptionExists();

        await _service.ReconcileAsync(CustomerId, CancellationToken.None);

        Assert.False(stale.IsActiveAt(Now));

        // Consumers must read this as "disable the installed feature", never as
        // "uninstall it" (docs/adr/0016).
        await _events.Received(1).PublishAsync(
            Arg.Is<FeatureEntitlementRevoked>(e => e.FeatureId == featureId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconciliationLeavesManualGrantsAlone()
    {
        var featureId = Guid.NewGuid();
        var granted = FeatureEntitlement.Grant(
            Guid.NewGuid(),
            CustomerId,
            featureId,
            EntitlementSource.Grant,
            Now.AddDays(-10),
            grantedBy: Guid.NewGuid());

        _entitlements.ListForCustomerAsync(CustomerId, false, Arg.Any<CancellationToken>()).Returns([granted]);
        _plans.GetOfferingAsync(PlanId, Arg.Any<CancellationToken>()).Returns(new PlanOffering(PlanId, "basic", "Basic", []));
        SubscriptionExists();

        await _service.ReconcileAsync(CustomerId, CancellationToken.None);

        // A plan change is not a decision to withdraw a grant made deliberately
        // outside the plan.
        Assert.True(granted.IsActiveAt(Now));
    }

    [Fact]
    public async Task ReconciliationRevokesEverythingWhenTheSubscriptionStopsEntitling()
    {
        var featureId = Guid.NewGuid();
        var held = FeatureEntitlement.Grant(Guid.NewGuid(), CustomerId, featureId, EntitlementSource.Plan, Now.AddDays(-10));

        _entitlements.ListForCustomerAsync(CustomerId, false, Arg.Any<CancellationToken>()).Returns([held]);
        SubscriptionExists(entitling: false);

        await _service.ReconcileAsync(CustomerId, CancellationToken.None);

        Assert.False(held.IsActiveAt(Now));
    }

    [Fact]
    public async Task ReconciliationDoesNotGrantAFeatureTheCustomerCannotRun()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId, dedicated: true));
        PlanOffers(featureId, isIncluded: true, isCustomerToggleable: false);
        SubscriptionExists();

        await _service.ReconcileAsync(CustomerId, CancellationToken.None);

        await _entitlements.DidNotReceive().AddAsync(Arg.Any<FeatureEntitlement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReconciliationIsIdempotent()
    {
        var featureId = Guid.NewGuid();
        var held = FeatureEntitlement.Grant(Guid.NewGuid(), CustomerId, featureId, EntitlementSource.Plan, Now.AddDays(-1));

        FeatureIs(featureId, Descriptor(featureId));
        _entitlements.ListForCustomerAsync(CustomerId, false, Arg.Any<CancellationToken>()).Returns([held]);
        PlanOffers(featureId, isIncluded: true, isCustomerToggleable: false);
        SubscriptionExists();

        await _service.ReconcileAsync(CustomerId, CancellationToken.None);
        await _service.ReconcileAsync(CustomerId, CancellationToken.None);

        Assert.True(held.IsActiveAt(Now));
        await _entitlements.DidNotReceive().AddAsync(Arg.Any<FeatureEntitlement>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AWithdrawnFeatureIsNotReportedAsEntitledEvenWhereTheRowSurvives()
    {
        var featureId = Guid.NewGuid();
        var held = FeatureEntitlement.Grant(Guid.NewGuid(), CustomerId, featureId, EntitlementSource.Plan, Now.AddDays(-1));

        _entitlements.FindActiveAsync(CustomerId, featureId, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(held);
        FeatureIs(featureId, Descriptor(featureId, status: "Withdrawn", canBeEntitled: false, remainsEntitled: false));

        Assert.False(await _service.IsEntitledAsync(CustomerId, featureId, CancellationToken.None));
    }

    [Fact]
    public async Task AManualGrantRecordsWhoMadeItAndRaisesTheEvent()
    {
        var featureId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId));

        var view = await _service.GrantAsync(CustomerId, featureId, actorId, null, CancellationToken.None);

        Assert.Equal(nameof(EntitlementSource.Grant), view.Source);
        await _entitlements.Received(1).AddAsync(
            Arg.Is<FeatureEntitlement>(e => e.GrantedBy == actorId),
            Arg.Any<CancellationToken>());
        await _events.Received(1).PublishAsync(Arg.Any<FeatureEntitlementGranted>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AManualGrantStillObeysTheInfrastructureRule()
    {
        var featureId = Guid.NewGuid();
        FeatureIs(featureId, Descriptor(featureId, dedicated: true));

        // Platform staff may grant outside a plan; they may not grant a
        // capability that cannot run.
        await Assert.ThrowsAsync<Knight.Application.Exceptions.ConflictException>(() =>
            _service.GrantAsync(CustomerId, featureId, Guid.NewGuid(), null, CancellationToken.None));
    }
}
