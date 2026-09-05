using Knight.Domain.Common;
using Knight.Domain.Exceptions;
using NSubstitute;
using Plans;
using Plans.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class PricingCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IFeaturePriceRepository _prices = Substitute.For<IFeaturePriceRepository>();
    private readonly IPricingCalculator _calculator;

    public PricingCalculatorTests()
    {
        // The calculator is internal to its module; the test project is a friend
        // assembly so the arithmetic can be exercised directly rather than only
        // through an endpoint.
        _calculator = new PricingCalculator(_prices);
    }

    private static Plan CreatePlan(decimal basePrice = 49m) =>
        Plan.Create(Guid.NewGuid(), Now, "basic", "Basic", Money.Of(basePrice, "EUR"), 1);

    private void PriceIs(Guid featureId, decimal amount, Guid? planId = null) =>
        _prices.GetApplicableAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([
                FeaturePrice.Create(Guid.NewGuid(), featureId, planId, Money.Of(amount, "EUR"), BillingPeriod.Monthly, Now),
            ]);

    [Fact]
    public async Task ThePlanAloneCostsItsBasePrice()
    {
        var plan = CreatePlan();

        var quote = await _calculator.QuoteAsync(new QuoteRequest(plan, [], Now), CancellationToken.None);

        Assert.Equal(49m, quote.Subtotal.Amount);
        Assert.Equal("EUR", quote.Currency);
        Assert.Single(quote.Lines);
    }

    [Fact]
    public async Task AnIncludedFeatureAddsNothing()
    {
        var plan = CreatePlan();
        var featureId = Guid.NewGuid();
        plan.SetFeature(featureId, isIncluded: true, isCustomerToggleable: false, null, Now);

        var quote = await _calculator.QuoteAsync(new QuoteRequest(plan, [featureId], Now), CancellationToken.None);

        // Included means paid for by the base price, so no line and no charge.
        Assert.Equal(49m, quote.Subtotal.Amount);
        Assert.Single(quote.Lines);
    }

    [Fact]
    public async Task ASelectedFeatureIsAddedAtItsPrice()
    {
        var plan = CreatePlan();
        var featureId = Guid.NewGuid();
        plan.SetFeature(featureId, isIncluded: false, isCustomerToggleable: true, null, Now);
        PriceIs(featureId, 29m);

        var quote = await _calculator.QuoteAsync(new QuoteRequest(plan, [featureId], Now), CancellationToken.None);

        Assert.Equal(78m, quote.Subtotal.Amount);
        Assert.Equal(2, quote.Lines.Count);
        Assert.Contains(quote.Lines, line => line.FeatureId == featureId && line.Total.Amount == 29m);
    }

    [Fact]
    public async Task APlanScopedPriceWinsOverTheGeneralOne()
    {
        var plan = CreatePlan();
        var featureId = Guid.NewGuid();
        plan.SetFeature(featureId, isIncluded: false, isCustomerToggleable: true, null, Now);

        _prices.GetApplicableAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([
                FeaturePrice.Create(Guid.NewGuid(), featureId, null, Money.Of(29m, "EUR"), BillingPeriod.Monthly, Now),
                FeaturePrice.Create(Guid.NewGuid(), featureId, plan.Id, Money.Of(19m, "EUR"), BillingPeriod.Monthly, Now),
            ]);

        var quote = await _calculator.QuoteAsync(new QuoteRequest(plan, [featureId], Now), CancellationToken.None);

        Assert.Equal(68m, quote.Subtotal.Amount);
    }

    [Fact]
    public async Task AFeatureWithNoPriceIsRefusedRatherThanQuotedAsFree()
    {
        var plan = CreatePlan();
        var featureId = Guid.NewGuid();
        plan.SetFeature(featureId, isIncluded: false, isCustomerToggleable: true, null, Now);

        _prices.GetApplicableAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // Free is a decision somebody has to have made, not a fallback.
        var exception = await Assert.ThrowsAsync<DomainException>(() =>
            _calculator.QuoteAsync(new QuoteRequest(plan, [featureId], Now), CancellationToken.None));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public async Task TheCompositeFeaturePriceIsTheSumOfItsChosenParts()
    {
        // The Automatic Admin is sold as parts; the price a customer pays is the
        // sum of the parts they ticked, computed here with no special path
        // (docs/adr/0037-composed-pricing-and-sub-features.md).
        var plan = CreatePlan();
        var image = Guid.NewGuid();
        var caption = Guid.NewGuid();
        var telegram = Guid.NewGuid();
        foreach (var part in new[] { image, caption, telegram })
        {
            plan.SetFeature(part, isIncluded: false, isCustomerToggleable: true, null, Now);
        }

        _prices.GetApplicableAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([
                FeaturePrice.Create(Guid.NewGuid(), image, null, Money.Of(12m, "EUR"), BillingPeriod.Monthly, Now),
                FeaturePrice.Create(Guid.NewGuid(), caption, null, Money.Of(9m, "EUR"), BillingPeriod.Monthly, Now),
                FeaturePrice.Create(Guid.NewGuid(), telegram, null, Money.Of(6m, "EUR"), BillingPeriod.Monthly, Now),
            ]);

        var quote = await _calculator.QuoteAsync(
            new QuoteRequest(plan, [image, caption, telegram], Now), CancellationToken.None);

        // 49 base + 12 + 9 + 6.
        Assert.Equal(76m, quote.Subtotal.Amount);
        Assert.Equal(4, quote.Lines.Count);
    }

    [Fact]
    public async Task TheSameFeatureRequestedTwiceIsChargedOnce()
    {
        var plan = CreatePlan();
        var featureId = Guid.NewGuid();
        plan.SetFeature(featureId, isIncluded: false, isCustomerToggleable: true, null, Now);
        PriceIs(featureId, 29m);

        var quote = await _calculator.QuoteAsync(new QuoteRequest(plan, [featureId, featureId], Now), CancellationToken.None);

        Assert.Equal(78m, quote.Subtotal.Amount);
        Assert.Equal(2, quote.Lines.Count);
    }
}
