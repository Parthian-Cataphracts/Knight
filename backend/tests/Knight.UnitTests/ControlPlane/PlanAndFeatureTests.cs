using FeatureRegistry.Domain;
using Knight.Domain.Common;
using Knight.Domain.Exceptions;
using Plans.Domain;
using Xunit;

namespace Knight.UnitTests.ControlPlane;

public sealed class PlanAndFeatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Feature CreateFeature(bool dedicated = false) =>
        Feature.Create(Guid.NewGuid(), Now, "analytics", "Analytics", "Insight", true, dedicated);

    private static Plan CreatePlan() =>
        Plan.Create(Guid.NewGuid(), Now, "basic", "Basic", Money.Of(49m, "EUR"), 1);

    [Theory]
    [InlineData("Analytics", "analytics")]
    [InlineData("  knight-feature-ai  ", "knight-feature-ai")]
    public void FeatureSlugsAreNormalized(string input, string expected)
    {
        Assert.Equal(expected, FeatureSlug.Normalize(input));
    }

    [Theory]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    [InlineData("under_score")]
    [InlineData("has space")]
    [InlineData("")]
    public void AnInvalidFeatureSlugIsRefused(string slug)
    {
        Assert.Throws<DomainException>(() => FeatureSlug.Normalize(slug));
    }

    [Fact]
    public void AFeatureStartsAsADraftAndCannotBeEntitled()
    {
        var feature = CreateFeature();

        Assert.Equal(FeatureStatus.Draft, feature.Status);
        Assert.False(feature.CanBeEntitled);
        Assert.False(feature.RemainsEntitled);
    }

    [Fact]
    public void PublishingMakesAFeatureSellable()
    {
        var feature = CreateFeature();
        feature.Publish(Now);

        Assert.True(feature.CanBeEntitled);
        Assert.True(feature.RemainsEntitled);
    }

    [Fact]
    public void DeprecationStopsNewEntitlementsButHonoursExistingOnes()
    {
        var feature = CreateFeature();
        feature.Publish(Now);
        feature.Deprecate(Now);

        Assert.False(feature.CanBeEntitled);
        Assert.True(feature.RemainsEntitled);
    }

    [Fact]
    public void WithdrawalEndsEntitlementEntirely()
    {
        var feature = CreateFeature();
        feature.Publish(Now);
        feature.Withdraw(Now);

        Assert.False(feature.CanBeEntitled);
        Assert.False(feature.RemainsEntitled);
    }

    [Fact]
    public void TheInfrastructureRequirementCannotChangeAfterPublication()
    {
        var feature = CreateFeature();
        feature.Publish(Now);

        // Changing it later would invalidate entitlements that were legitimate
        // when they were granted.
        var exception = Assert.Throws<DomainException>(() => feature.SetInfrastructureRequirement(true, Now));

        Assert.Equal(DomainErrorCategory.Conflict, exception.Category);
    }

    [Fact]
    public void AWithdrawnFeatureCannotBeEdited()
    {
        var feature = CreateFeature();
        feature.Publish(Now);
        feature.Withdraw(Now);

        Assert.Throws<DomainException>(() => feature.UpdateMetadata("New", null, "Insight", Now));
    }

    [Theory]
    [InlineData("BASIC", "basic")]
    [InlineData(" professional ", "professional")]
    public void PlanKeysAreNormalized(string input, string expected)
    {
        var plan = Plan.Create(Guid.NewGuid(), Now, input, "Name", Money.Of(1m, "EUR"));

        Assert.Equal(expected, plan.Key);
    }

    [Fact]
    public void SettingAFeatureTwiceUpdatesRatherThanDuplicates()
    {
        var plan = CreatePlan();
        var featureId = Guid.NewGuid();

        plan.SetFeature(featureId, isIncluded: false, isCustomerToggleable: true, null, Now);
        plan.SetFeature(featureId, isIncluded: true, isCustomerToggleable: false, "^1.0.0", Now);

        var entry = Assert.Single(plan.Features);
        Assert.True(entry.IsIncluded);
        Assert.False(entry.IsCustomerToggleable);
        Assert.Equal("^1.0.0", entry.PinnedVersionRange);
    }

    [Fact]
    public void IncludedAndSelectableFeaturesAreDistinctSets()
    {
        var plan = CreatePlan();
        var included = Guid.NewGuid();
        var selectable = Guid.NewGuid();
        var lockedOff = Guid.NewGuid();

        plan.SetFeature(included, isIncluded: true, isCustomerToggleable: false, null, Now);
        plan.SetFeature(selectable, isIncluded: false, isCustomerToggleable: true, null, Now);
        plan.SetFeature(lockedOff, isIncluded: false, isCustomerToggleable: false, null, Now);

        Assert.Equal([included], plan.IncludedFeatureIds);
        Assert.Equal([selectable], plan.SelectableFeatureIds);
    }

    [Fact]
    public void DeactivatingAPlanDoesNotRemoveItsContents()
    {
        var plan = CreatePlan();
        plan.SetFeature(Guid.NewGuid(), true, false, null, Now);

        plan.Deactivate(Now);

        Assert.False(plan.IsActive);
        Assert.Single(plan.Features);
    }

    [Fact]
    public void APriceAppliesOnlyWithinItsWindow()
    {
        var price = FeaturePrice.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            planId: null,
            Money.Of(29m, "EUR"),
            BillingPeriod.Monthly,
            Now,
            Now.AddDays(30));

        Assert.False(price.AppliesAt(Now.AddDays(-1)));
        Assert.True(price.AppliesAt(Now));
        Assert.True(price.AppliesAt(Now.AddDays(29)));
        Assert.False(price.AppliesAt(Now.AddDays(30)));
    }

    [Fact]
    public void APlanScopedPriceIsMoreSpecificThanAGeneralOne()
    {
        var general = FeaturePrice.Create(Guid.NewGuid(), Guid.NewGuid(), null, Money.Of(29m, "EUR"), BillingPeriod.Monthly, Now);
        var scoped = FeaturePrice.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Money.Of(19m, "EUR"), BillingPeriod.Monthly, Now);

        Assert.True(scoped.Specificity > general.Specificity);
    }

    [Fact]
    public void ClosingAPriceTwiceIsRefused()
    {
        var price = FeaturePrice.Create(Guid.NewGuid(), Guid.NewGuid(), null, Money.Of(29m, "EUR"), BillingPeriod.Monthly, Now);
        price.Close(Now.AddDays(1));

        Assert.Throws<DomainException>(() => price.Close(Now.AddDays(2)));
    }

    [Fact]
    public void APriceCannotEndBeforeItStarts()
    {
        Assert.Throws<DomainException>(() => FeaturePrice.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            Money.Of(29m, "EUR"),
            BillingPeriod.Monthly,
            Now,
            Now.AddDays(-1)));
    }

    // --- Sub-features (composed pricing, adr/0037) ---------------------------

    [Fact]
    public void AFeatureIsTopLevelUntilItIsGroupedUnderAParent()
    {
        var feature = CreateFeature();

        Assert.False(feature.IsSubFeature);
        Assert.Null(feature.ParentFeatureId);
    }

    [Fact]
    public void GroupingAFeatureMakesItASubFeatureOfItsParent()
    {
        var parent = CreateFeature();
        var sub = CreateFeature();

        sub.GroupUnder(parent.Id, Now);

        Assert.True(sub.IsSubFeature);
        Assert.Equal(parent.Id, sub.ParentFeatureId);
    }

    [Fact]
    public void AFeatureCannotBeASubFeatureOfItself()
    {
        var feature = CreateFeature();

        Assert.Throws<DomainException>(() => feature.GroupUnder(feature.Id, Now));
    }

    [Fact]
    public void GroupingIsRefusedOnceTheFeatureIsPublished()
    {
        // Moving a sold Feature into a group would change what a customer's
        // selection totals to; only a draft may be grouped.
        var sub = CreateFeature();
        sub.Publish(Now);

        Assert.Throws<DomainException>(() => sub.GroupUnder(Guid.NewGuid(), Now));
    }

    [Fact]
    public void ASubFeatureCanBeDetachedWhileItIsStillADraft()
    {
        var sub = CreateFeature();
        sub.GroupUnder(Guid.NewGuid(), Now);

        sub.Ungroup(Now);

        Assert.False(sub.IsSubFeature);
        Assert.Null(sub.ParentFeatureId);
    }
}
