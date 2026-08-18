using NSubstitute;
using Knight.Application.Abstractions.Time;
using Knight.Application.Exceptions;
using Promotions;
using Promotions.Domain;
using Xunit;

namespace Knight.UnitTests.Promotions;

public sealed class PromotionEvaluationServiceTests
{
    private readonly IPromotionRepository _repo = Substitute.For<IPromotionRepository>();
    private readonly IDateTimeProvider _timeProvider = Substitute.For<IDateTimeProvider>();
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public PromotionEvaluationServiceTests()
    {
        _timeProvider.UtcNow.Returns(_now);
    }

    [Fact]
    public async Task EvaluateAsync_AutomaticPromotions_PicksHighestDiscount()
    {
        var promo10 = Promotion.Create(Guid.NewGuid(), _tenantId, "10%", null, PromotionDiscountType.Percentage, 10m, null, null, null, null, false, 0, _now);
        promo10.Activate(_now);

        var promo25 = Promotion.Create(Guid.NewGuid(), _tenantId, "25%", null, PromotionDiscountType.Percentage, 25m, null, null, null, null, false, 0, _now);
        promo25.Activate(_now);

        _repo.GetActiveAutomaticPromotionsAsync(_tenantId, _now, Arg.Any<CancellationToken>())
            .Returns([promo10, promo25]);

        var service = new PromotionEvaluationService(_repo, _timeProvider);
        var result = await service.EvaluateAsync(_tenantId, 100m, null, CancellationToken.None);

        Assert.True(result.HasDiscount);
        Assert.Equal(25m, result.DiscountTotal);
        Assert.Equal(promo25.Id, result.AppliedPromotion!.PromotionId);
    }

    [Fact]
    public async Task EvaluateAsync_AutomaticPromotions_TieBreakPriority_HigherPriorityWins()
    {
        var promoA = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo A", null, PromotionDiscountType.FixedAmount, 20m, null, null, null, null, false, 1, _now);
        promoA.Activate(_now);

        var promoB = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo B", null, PromotionDiscountType.FixedAmount, 20m, null, null, null, null, false, 10, _now);
        promoB.Activate(_now);

        _repo.GetActiveAutomaticPromotionsAsync(_tenantId, _now, Arg.Any<CancellationToken>())
            .Returns([promoA, promoB]);

        var service = new PromotionEvaluationService(_repo, _timeProvider);
        var result = await service.EvaluateAsync(_tenantId, 100m, null, CancellationToken.None);

        Assert.True(result.HasDiscount);
        Assert.Equal(20m, result.DiscountTotal);
        Assert.Equal(promoB.Id, result.AppliedPromotion!.PromotionId); // Priority 10 wins over Priority 1
    }

    [Fact]
    public async Task EvaluateAsync_CouponProvided_EvaluatesLinkedPromotionOnly()
    {
        var promo = Promotion.Create(Guid.NewGuid(), _tenantId, "Coupon Promo", null, PromotionDiscountType.Percentage, 30m, null, null, null, null, true, 0, _now);
        promo.Activate(_now);

        var coupon = Coupon.Create(Guid.NewGuid(), _tenantId, promo.Id, "SAVE30", null, null, null, _now);

        _repo.GetCouponByNormalizedCodeAsync(_tenantId, "SAVE30", Arg.Any<CancellationToken>())
            .Returns(coupon);
        _repo.GetByIdAsync(_tenantId, promo.Id, Arg.Any<CancellationToken>())
            .Returns(promo);

        var service = new PromotionEvaluationService(_repo, _timeProvider);
        var result = await service.EvaluateAsync(_tenantId, 100m, "  save30  ", CancellationToken.None);

        Assert.True(result.HasDiscount);
        Assert.Equal(30m, result.DiscountTotal);
        Assert.Equal(promo.Id, result.AppliedPromotion!.PromotionId);
        Assert.Equal("SAVE30", result.AppliedPromotion.CouponCode);
    }

    [Fact]
    public async Task EvaluateAsync_InvalidCoupon_ThrowsValidationException()
    {
        _repo.GetCouponByNormalizedCodeAsync(_tenantId, "INVALID", Arg.Any<CancellationToken>())
            .Returns((Coupon?)null);

        var service = new PromotionEvaluationService(_repo, _timeProvider);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.EvaluateAsync(_tenantId, 100m, "INVALID", CancellationToken.None));
    }

    [Fact]
    public async Task EvaluateAsync_ExhaustedCoupon_ThrowsValidationException()
    {
        var promo = Promotion.Create(Guid.NewGuid(), _tenantId, "Promo", null, PromotionDiscountType.Percentage, 20m, null, null, null, null, true, 0, _now);
        promo.Activate(_now);

        var coupon = Coupon.Create(Guid.NewGuid(), _tenantId, promo.Id, "LIMIT1", 1, null, null, _now);

        _repo.GetCouponByNormalizedCodeAsync(_tenantId, "LIMIT1", Arg.Any<CancellationToken>())
            .Returns(coupon);
        _repo.GetCouponRedemptionCountAsync(_tenantId, coupon.Id, Arg.Any<CancellationToken>())
            .Returns(1); // Already 1 used out of limit 1

        var service = new PromotionEvaluationService(_repo, _timeProvider);

        await Assert.ThrowsAsync<ValidationException>(() =>
            service.EvaluateAsync(_tenantId, 100m, "LIMIT1", CancellationToken.None));
    }
}
