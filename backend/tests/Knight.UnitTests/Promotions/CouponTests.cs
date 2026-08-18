using Knight.Application.Exceptions;
using Promotions.Domain;
using Xunit;

namespace Knight.UnitTests.Promotions;

public sealed class CouponTests
{
    private readonly Guid _tenantId = Guid.NewGuid();
    private readonly Guid _promotionId = Guid.NewGuid();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public void NormalizeCode_TrimsAndConvertsToUppercase()
    {
        Assert.Equal("SUMMER20", Coupon.NormalizeCode("  summer20  "));
        Assert.Equal("SAVE-10", Coupon.NormalizeCode("save-10"));
        Assert.Equal("", Coupon.NormalizeCode("   "));
        Assert.Equal("", Coupon.NormalizeCode(null!));
    }

    [Fact]
    public void Create_Coupon_NormalizesCodeAndSetsActive()
    {
        var coupon = Coupon.Create(
            Guid.NewGuid(),
            _tenantId,
            _promotionId,
            "  save50  ",
            100,
            null,
            null,
            _now);

        Assert.Equal("save50", coupon.Code);
        Assert.Equal("SAVE50", coupon.NormalizedCode);
        Assert.Equal(CouponStatus.Active, coupon.Status);
        Assert.Equal(100, coupon.UsageLimitTotal);
    }

    [Fact]
    public void Create_Coupon_ValidatesNonPositiveUsageLimit()
    {
        Assert.Throws<ValidationException>(() =>
            Coupon.Create(Guid.NewGuid(), _tenantId, _promotionId, "CODE", 0, null, null, _now));

        Assert.Throws<ValidationException>(() =>
            Coupon.Create(Guid.NewGuid(), _tenantId, _promotionId, "CODE", -5, null, null, _now));
    }

    [Fact]
    public void IsActiveAt_ChecksTimeBoundariesAndArchiveState()
    {
        var starts = _now;
        var ends = _now.AddDays(1);

        var coupon = Coupon.Create(
            Guid.NewGuid(),
            _tenantId,
            _promotionId,
            "CODE",
            null,
            starts,
            ends,
            _now);

        Assert.False(coupon.IsActiveAt(starts.AddSeconds(-1)));
        Assert.True(coupon.IsActiveAt(starts));
        Assert.True(coupon.IsActiveAt(starts.AddHours(12)));
        Assert.False(coupon.IsActiveAt(ends));

        coupon.Archive(_now);
        Assert.False(coupon.IsActiveAt(starts.AddHours(12)));

        Assert.Throws<ConflictException>(() =>
            coupon.Update(50, null, null, _now));
    }
}
