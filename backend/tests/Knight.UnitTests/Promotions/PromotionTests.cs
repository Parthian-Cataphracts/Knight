using Knight.Application.Exceptions;
using Promotions.Domain;
using Xunit;

namespace Knight.UnitTests.Promotions;

public sealed class PromotionTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void Create_PercentagePromotion_ValidatesDiscountValueRange()
    {
        Assert.Throws<ValidationException>(() =>
            Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 0m, null, null, null, null, false, 0, _now));

        Assert.Throws<ValidationException>(() =>
            Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 101m, null, null, null, null, false, 0, _now));

        var valid = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 20m, null, null, null, null, false, 0, _now);
        Assert.Equal(20m, valid.DiscountValue);
        Assert.Equal(PromotionStatus.Draft, valid.Status);
    }

    [Fact]
    public void Create_FixedPromotion_ValidatesPositiveValue()
    {
        Assert.Throws<ValidationException>(() =>
            Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.FixedAmount, 0m, null, null, null, null, false, 0, _now));

        Assert.Throws<ValidationException>(() =>
            Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.FixedAmount, -5m, null, null, null, null, false, 0, _now));

        var valid = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.FixedAmount, 15m, null, null, null, null, false, 0, _now);
        Assert.Equal(15m, valid.DiscountValue);
    }

    [Fact]
    public void Create_TimeWindow_StartsAtMustBeBeforeEndsAt()
    {
        var starts = _now.AddDays(2);
        var ends = _now.AddDays(1);

        Assert.Throws<ValidationException>(() =>
            Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 10m, null, null, starts, ends, false, 0, _now));

        var valid = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 10m, null, null, ends, starts, false, 0, _now);
        Assert.Equal(ends, valid.StartsAt);
        Assert.Equal(starts, valid.EndsAt);
    }

    [Fact]
    public void CalculateDiscount_Percentage_CalculatesExactRoundedAmount()
    {
        var promo = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 15m, null, null, null, null, false, 0, _now);
        promo.Activate(_now);

        // 99.99 * 0.15 = 14.9985 -> rounded to 15.00
        var discount = promo.CalculateDiscount(99.99m, _now);
        Assert.Equal(15.00m, discount);
    }

    [Fact]
    public void CalculateDiscount_Percentage_AppliesMaxDiscountCap()
    {
        var promo = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 50m, null, 25m, null, null, false, 0, _now);
        promo.Activate(_now);

        // 100 * 50% = 50, but max cap is 25
        var discount = promo.CalculateDiscount(100m, _now);
        Assert.Equal(25m, discount);
    }

    [Fact]
    public void CalculateDiscount_Fixed_CappedAtSubtotal()
    {
        var promo = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.FixedAmount, 50m, null, null, null, null, false, 0, _now);
        promo.Activate(_now);

        // Subtotal 30, fixed discount 50 -> discount capped at 30
        var discount = promo.CalculateDiscount(30m, _now);
        Assert.Equal(30m, discount);
    }

    [Fact]
    public void CalculateDiscount_MinimumSubtotalRequirement()
    {
        var promo = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 20m, 100m, null, null, null, false, 0, _now);
        promo.Activate(_now);

        // Subtotal 80 < MinimumSubtotal 100 -> 0 discount
        Assert.Equal(0m, promo.CalculateDiscount(80m, _now));

        // Subtotal 100 >= MinimumSubtotal 100 -> 20 discount
        Assert.Equal(20m, promo.CalculateDiscount(100m, _now));
    }

    [Fact]
    public void CalculateDiscount_TimeBoundaries_InclusiveStart_ExclusiveEnd()
    {
        var starts = _now;
        var ends = _now.AddDays(1);

        var promo = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 10m, null, null, starts, ends, false, 0, _now);
        promo.Activate(_now);

        // Before start -> 0
        Assert.Equal(0m, promo.CalculateDiscount(100m, starts.AddSeconds(-1)));

        // At start (inclusive) -> eligible
        Assert.Equal(10m, promo.CalculateDiscount(100m, starts));

        // Within window -> eligible
        Assert.Equal(10m, promo.CalculateDiscount(100m, starts.AddHours(12)));

        // At end (exclusive) -> ineligible
        Assert.Equal(0m, promo.CalculateDiscount(100m, ends));

        // After end -> ineligible
        Assert.Equal(0m, promo.CalculateDiscount(100m, ends.AddSeconds(1)));
    }

    [Fact]
    public void Lifecycle_DraftAndArchived_AreNotEligible()
    {
        var promo = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 10m, null, null, null, null, false, 0, _now);

        // Draft -> 0
        Assert.False(promo.IsEligible(100m, _now));
        Assert.Equal(0m, promo.CalculateDiscount(100m, _now));

        promo.Activate(_now);
        Assert.True(promo.IsEligible(100m, _now));
        Assert.Equal(10m, promo.CalculateDiscount(100m, _now));

        promo.Archive(_now);
        Assert.False(promo.IsEligible(100m, _now));
        Assert.Equal(0m, promo.CalculateDiscount(100m, _now));

        // Cannot update archived promotion
        Assert.Throws<ConflictException>(() =>
            promo.Update("New Name", null, PromotionDiscountType.Percentage, 10m, null, null, null, null, false, 0, _now));
    }
}
