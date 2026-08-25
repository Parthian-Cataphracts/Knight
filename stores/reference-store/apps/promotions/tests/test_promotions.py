"""
Coupons and discounts, now that they are the base store's.

These are the tests that used to live in the store suite behind a
`skipUnless(PROMOTIONS_INSTALLED)` guard, because the rules were an optional
Feature. They run unconditionally now, which is the whole point of the move: a
capability every store has should not have a suite that quietly skips
([`adr/0024`](../../../../../docs/adr/0024-base-store-versus-optional-feature.md)).
"""

from decimal import Decimal

from django.test import TestCase
from django.utils import timezone

from apps.promotions import services as promotions
from apps.promotions.models import Coupon, DiscountType, Promotion, PromotionStatus


class PromotionPricingTests(TestCase):
    def _promotion(self, **overrides) -> Promotion:
        fields = {
            "name": "Ramadan 20%",
            "status": PromotionStatus.ACTIVE,
            "discount_type": DiscountType.PERCENTAGE,
            "discount_value": Decimal("20"),
            "requires_coupon": False,
        }
        fields.update(overrides)

        return Promotion.objects.create(**fields)

    def test_a_percentage_takes_the_right_amount(self):
        promotion = self._promotion()

        self.assertEqual(promotion.discount_for(Decimal("100000")), Decimal("20000"))

    def test_a_fixed_amount_is_taken_as_given(self):
        promotion = self._promotion(
            discount_type=DiscountType.FIXED_AMOUNT, discount_value=Decimal("15000")
        )

        self.assertEqual(promotion.discount_for(Decimal("100000")), Decimal("15000"))

    def test_a_discount_never_exceeds_the_basket(self):
        # A store that owes a shopper money is a refund, not a negative order.
        promotion = self._promotion(
            discount_type=DiscountType.FIXED_AMOUNT, discount_value=Decimal("500000")
        )

        self.assertEqual(promotion.discount_for(Decimal("100000")), Decimal("100000"))

    def test_a_basket_below_the_minimum_gets_nothing(self):
        promotion = self._promotion(minimum_subtotal=Decimal("200000"))

        self.assertEqual(promotion.discount_for(Decimal("100000")), Decimal("0"))

    def test_a_percentage_is_capped_where_a_cap_is_set(self):
        # The classic way a campaign costs more than intended: a percentage
        # discount meeting an unexpectedly large basket.
        promotion = self._promotion(maximum_discount_amount=Decimal("10000"))

        self.assertEqual(promotion.discount_for(Decimal("1000000")), Decimal("10000"))

    def test_a_draft_promotion_is_not_live(self):
        promotion = self._promotion(status=PromotionStatus.DRAFT)

        self.assertFalse(promotion.is_live())

    def test_a_promotion_outside_its_window_is_not_live(self):
        now = timezone.now()
        promotion = self._promotion(
            starts_at=now - timezone.timedelta(days=10),
            ends_at=now - timezone.timedelta(days=1),
        )

        self.assertFalse(promotion.is_live(now))

    def test_a_percentage_over_a_hundred_is_refused(self):
        # Otherwise the order aggregate clamps it to zero and the mistake is
        # invisible rather than refused.
        promotion = Promotion(
            name="Broken",
            discount_type=DiscountType.PERCENTAGE,
            discount_value=Decimal("150"),
        )

        with self.assertRaises(Exception):
            promotion.full_clean()

    def test_the_best_automatic_promotion_wins(self):
        self._promotion(name="Small", discount_type=DiscountType.FIXED_AMOUNT, discount_value=Decimal("5000"))
        self._promotion(name="Large", discount_type=DiscountType.FIXED_AMOUNT, discount_value=Decimal("25000"))

        outcome = promotions.price(Decimal("100000"))

        self.assertEqual(outcome.promotion_name, "Large")
        self.assertEqual(outcome.discount_amount, Decimal("25000"))

    def test_a_presented_code_is_honoured_over_a_better_automatic_one(self):
        # A shopper who typed a code expects that code to be used. Silently
        # substituting a better offer is a support call even though it saved
        # them money.
        self._promotion(name="Auto", discount_type=DiscountType.FIXED_AMOUNT, discount_value=Decimal("50000"))

        coded = self._promotion(
            name="Coded",
            requires_coupon=True,
            discount_type=DiscountType.FIXED_AMOUNT,
            discount_value=Decimal("10000"),
        )
        Coupon.objects.create(promotion=coded, code="ramadan20")

        outcome = promotions.price(Decimal("100000"), coupon_code="  RaMaDaN20 ")

        self.assertEqual(outcome.promotion_name, "Coded")
        self.assertEqual(outcome.discount_amount, Decimal("10000"))

    def test_an_unknown_code_yields_nothing_rather_than_raising(self):
        outcome = promotions.price(Decimal("100000"), coupon_code="NOPE")

        self.assertFalse(outcome.applies)

    def test_a_coupon_cannot_outlive_its_promotion(self):
        now = timezone.now()
        expired = self._promotion(
            requires_coupon=True,
            ends_at=now - timezone.timedelta(days=1),
        )
        coupon = Coupon.objects.create(promotion=expired, code="OLD")

        self.assertFalse(coupon.is_redeemable(now))

    def test_a_usage_limit_is_enforced(self):
        promotion = self._promotion(requires_coupon=True)
        coupon = Coupon.objects.create(promotion=promotion, code="ONCE", usage_limit_total=1)

        self.assertTrue(promotions.redeem(coupon.pk, source_order_id=1))
        self.assertFalse(coupon.is_redeemable())

    def test_redeeming_the_same_order_twice_counts_once(self):
        # Two concurrent checkouts both reading "not yet redeemed" is exactly how
        # a limited campaign gets over-redeemed; the constraint settles it.
        promotion = self._promotion(requires_coupon=True)
        coupon = Coupon.objects.create(promotion=promotion, code="TWICE", usage_limit_total=5)

        self.assertTrue(promotions.redeem(coupon.pk, source_order_id=7))
        self.assertFalse(promotions.redeem(coupon.pk, source_order_id=7))
        self.assertEqual(coupon.times_redeemed, 1)


class SnapshotSurvivesTheRuleTests(TestCase):
    """
    The property the whole base/Feature split rests on, and the reason it still
    matters now that coupons are base.

    It was written so an order priced by a Feature stayed readable once the
    Feature was gone. The same property is what lets a rule *move* — from the
    Feature into this app — without a single historical receipt changing.
    """

    def test_an_order_keeps_its_discount_after_the_promotion_is_deleted(self):
        from apps.orders.models import Order, OrderPromotion

        promotion = Promotion.objects.create(
            name="Ramadan 20%",
            status=PromotionStatus.ACTIVE,
            discount_type=DiscountType.PERCENTAGE,
            discount_value=Decimal("20"),
            requires_coupon=False,
        )

        outcome = promotions.price(Decimal("100000"))

        order = Order.place(
            subtotal=Decimal("100000"),
            discount_total=outcome.discount_amount,
            total=Decimal("80000"),
        )

        OrderPromotion.objects.create(
            order=order,
            source_promotion_id=outcome.promotion_id,
            promotion_name=outcome.promotion_name,
            coupon_code=outcome.coupon_code,
            discount_type=outcome.discount_type,
            discount_value=outcome.discount_value,
            discount_amount=outcome.discount_amount,
        )

        # The rule goes: archived, deleted, or carried off with an uninstalled
        # Feature. The receipt must not care which.
        promotion.delete()

        order.refresh_from_db()

        self.assertEqual(order.promotion.promotion_name, "Ramadan 20%")
        self.assertEqual(order.promotion.discount_amount, Decimal("20000"))
        self.assertEqual(order.total, Decimal("80000"))
