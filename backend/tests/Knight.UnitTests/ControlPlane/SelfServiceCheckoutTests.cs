using FeatureRegistry.Domain;
using Knight.Application.Abstractions.ControlPlane;
using Knight.Application.Abstractions.Time;
using Knight.Domain.Common;
using Microsoft.Extensions.Options;
using NSubstitute;
using Plans;
using Plans.Domain;
using PlatformBilling;
using PlatformBilling.Domain;
using PlatformBilling.Payments;
using Subscriptions.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

/// <summary>
/// The self-service checkout's invariants (docs/self-service-saas-plan.md §6):
/// only a publicly purchasable plan can be bought, only features the plan offers
/// can be selected, the price is computed server-side, and the subscription it
/// creates is Pending — entitling nothing until a payment activates it.
/// </summary>
public sealed class SelfServiceCheckoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IPlanRepository _plans = Substitute.For<IPlanRepository>();
    private readonly IFeatureRepository _features = Substitute.For<IFeatureRepository>();
    private readonly IPricingCalculator _pricing = Substitute.For<IPricingCalculator>();
    private readonly ISubscriptionRepository _subscriptions = Substitute.For<ISubscriptionRepository>();
    private readonly ICheckoutSessionRepository _sessions = Substitute.For<ICheckoutSessionRepository>();
    private readonly IPlatformBillingTransactionRepository _transactions = Substitute.For<IPlatformBillingTransactionRepository>();
    private readonly IAuditTrail _audit = Substitute.For<IAuditTrail>();
    private readonly IDateTimeProvider _clock = Substitute.For<IDateTimeProvider>();
    private readonly CheckoutService _checkout;

    public SelfServiceCheckoutTests()
    {
        _clock.UtcNow.Returns(Now);

        var options = Options.Create(new PlatformBillingOptions { DefaultProvider = "simulated", WebhookSecret = string.Empty });
        var registry = new PlatformPaymentProviderRegistry([new SimulatedPaymentProvider(options)]);

        _checkout = new CheckoutService(
            _plans, _features, _pricing, _subscriptions, _sessions, _transactions, registry, _audit, _clock, options);
    }

    private Plan PurchasablePlan(decimal basePrice = 49m)
    {
        var plan = Plan.Create(Guid.NewGuid(), Now, "basic", "Basic", Money.Of(basePrice, "EUR"), 1);
        plan.SetPubliclyPurchasable(true, Now);
        return plan;
    }

    private static Feature PublishedFeature()
    {
        var feature = Feature.Create(Guid.NewGuid(), Now, "analytics", "Analytics", "Insight", true, false);
        feature.Publish(Now);
        return feature;
    }

    private void Prices(decimal subtotal) =>
        _pricing.QuoteAsync(Arg.Any<QuoteRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PriceQuote(Money.Of(subtotal, "EUR"), []));

    [Fact]
    public async Task CheckoutOpensAPendingSubscriptionAtTheComputedPrice()
    {
        var plan = PurchasablePlan();
        _plans.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);
        Prices(49m);

        Subscription? saved = null;
        await _subscriptions.AddAsync(Arg.Do<Subscription>(s => saved = s), Arg.Any<CancellationToken>());

        var customerId = Guid.NewGuid();
        var result = await _checkout.CheckoutAsync(
            new CheckoutRequest(customerId, plan.Id, BillingInterval.Monthly, [], null), CancellationToken.None);

        Assert.Equal(49m, result.Amount);
        Assert.Equal("EUR", result.Currency);
        Assert.StartsWith("https://", result.CheckoutUrl);
        Assert.NotNull(saved);
        Assert.Equal(SubscriptionStatus.Pending, saved!.Status);
        Assert.False(saved.IsEntitling);
        Assert.Equal("simulated", saved.Provider);
    }

    [Fact]
    public async Task APlanNotOnPublicSaleIsRefused()
    {
        var plan = Plan.Create(Guid.NewGuid(), Now, "internal", "Internal", Money.Of(0m, "EUR"), 1);
        _plans.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var exception = await Assert.ThrowsAsync<SelfServiceBillingException>(() =>
            _checkout.CheckoutAsync(new CheckoutRequest(Guid.NewGuid(), plan.Id, BillingInterval.Monthly, [], null), CancellationToken.None));

        Assert.Equal("PLAN_UNAVAILABLE", exception.ErrorCode);
    }

    [Fact]
    public async Task AFeatureThePlanDoesNotOfferIsRefused()
    {
        var plan = PurchasablePlan();
        _plans.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var exception = await Assert.ThrowsAsync<SelfServiceBillingException>(() =>
            _checkout.CheckoutAsync(
                new CheckoutRequest(Guid.NewGuid(), plan.Id, BillingInterval.Monthly, [Guid.NewGuid()], null),
                CancellationToken.None));

        Assert.Equal("INVALID_FEATURE_SELECTION", exception.ErrorCode);
    }

    [Fact]
    public async Task ADraftFeatureOfferedByThePlanStillCannotBeBought()
    {
        var plan = PurchasablePlan();
        var featureId = Guid.NewGuid();
        plan.SetFeature(featureId, isIncluded: false, isCustomerToggleable: true, null, Now);
        _plans.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        // Offered by the plan, but not published — so not entitlable, so not sellable.
        var draft = Feature.Create(featureId, Now, "draft", "Draft", "x", true, false);
        _features.GetByIdAsync(featureId, Arg.Any<CancellationToken>()).Returns(draft);

        var exception = await Assert.ThrowsAsync<SelfServiceBillingException>(() =>
            _checkout.CheckoutAsync(
                new CheckoutRequest(Guid.NewGuid(), plan.Id, BillingInterval.Monthly, [featureId], null),
                CancellationToken.None));

        Assert.Equal("INVALID_FEATURE_SELECTION", exception.ErrorCode);
    }

    [Fact]
    public async Task ASelectedOptionalFeatureIsEnabledOnTheSubscription()
    {
        var plan = PurchasablePlan();
        var featureId = Guid.NewGuid();
        plan.SetFeature(featureId, isIncluded: false, isCustomerToggleable: true, null, Now);
        _plans.GetByIdAsync(plan.Id, Arg.Any<CancellationToken>()).Returns(plan);

        var feature = PublishedFeature();
        _features.GetByIdAsync(featureId, Arg.Any<CancellationToken>()).Returns(feature);
        Prices(78m);

        Subscription? saved = null;
        await _subscriptions.AddAsync(Arg.Do<Subscription>(s => saved = s), Arg.Any<CancellationToken>());

        await _checkout.CheckoutAsync(
            new CheckoutRequest(Guid.NewGuid(), plan.Id, BillingInterval.Monthly, [featureId], null), CancellationToken.None);

        Assert.NotNull(saved);
        Assert.Contains(featureId, saved!.EnabledFeatureIds);
    }
}
