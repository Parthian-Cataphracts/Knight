"""
The two optional Features, and the property that makes them optional.

These live in the store's test suite rather than in each package because what is
being checked is the *seam*: that promotions and delivery price correctly, and
that an order priced by them stays explicable once they are gone
([`adr/0024`](../../../../../docs/adr/0024-base-store-versus-optional-feature.md)).

Skipped when the packages are not installed, which is the honest thing for a
suite that must also pass on a base store with no Features on it.
"""

from decimal import Decimal
from unittest import skipUnless

from django.test import TestCase
from django.utils import timezone

try:  # pragma: no cover - the import itself is the capability check
    from knight_feature_promotions import services as promotions
    from knight_feature_promotions.models import Coupon, DiscountType, Promotion, PromotionStatus

    PROMOTIONS_INSTALLED = True
except ImportError:  # pragma: no cover
    PROMOTIONS_INSTALLED = False

try:  # pragma: no cover
    from knight_feature_delivery import services as delivery
    from knight_feature_delivery.models import DeliverySettings, DeliveryZone

    DELIVERY_INSTALLED = True
except ImportError:  # pragma: no cover
    DELIVERY_INSTALLED = False


@skipUnless(PROMOTIONS_INSTALLED, "The promotions Feature is not installed.")
class PromotionPricingTests(TestCase):
    def _promotion(self, **overrides) -> "Promotion":
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


@skipUnless(DELIVERY_INSTALLED, "The delivery Feature is not installed.")
class DeliveryQuotingTests(TestCase):
    def setUp(self):
        self.zone = DeliveryZone.objects.create(name="Central", fee=Decimal("30000"))

    def test_a_zone_quotes_its_fee(self):
        quote = delivery.quote(self.zone.pk, Decimal("100000"))

        self.assertTrue(quote.accepted)
        self.assertEqual(quote.fee, Decimal("30000"))

    def test_an_unknown_zone_is_refused_with_a_reason(self):
        # "We do not deliver there" and "your basket is too small" lead to
        # completely different next actions for the shopper.
        quote = delivery.quote(999999, Decimal("100000"))

        self.assertFalse(quote.accepted)
        self.assertIn("not available", quote.reason)

    def test_a_basket_below_the_zone_minimum_is_refused(self):
        self.zone.minimum_order_subtotal = Decimal("200000")
        self.zone.save()

        quote = delivery.quote(self.zone.pk, Decimal("100000"))

        self.assertFalse(quote.accepted)
        self.assertIn("Central", quote.reason)

    def test_a_zone_minimum_overrides_the_store_default(self):
        # A far suburb that only makes sense above a larger basket is what this
        # exists for; two figures combined would be impossible to explain.
        settings = DeliverySettings.current()
        settings.default_minimum_order = Decimal("500000")
        settings.save()

        self.zone.minimum_order_subtotal = Decimal("50000")
        self.zone.save()

        self.assertTrue(delivery.quote(self.zone.pk, Decimal("100000")).accepted)

    def test_pausing_deliveries_refuses_without_changing_the_zones(self):
        # A kitchen stopping for an hour should not have to reconfigure, and
        # turning it back on must restore exactly what was there.
        settings = DeliverySettings.current()
        settings.is_accepting_orders = False
        settings.save()

        self.assertFalse(delivery.quote(self.zone.pk, Decimal("100000")).accepted)

        settings.is_accepting_orders = True
        settings.save()

        self.assertTrue(delivery.quote(self.zone.pk, Decimal("100000")).accepted)

    def test_an_archived_zone_frees_its_name(self):
        # A business reorganising its areas should not have to invent a name it
        # has already used.
        self.zone.archive()
        self.zone.save()

        DeliveryZone.objects.create(name="Central", fee=Decimal("40000"))

        self.assertEqual(DeliveryZone.objects.filter(name="Central").count(), 2)


@skipUnless(PROMOTIONS_INSTALLED, "The promotions Feature is not installed.")
class SnapshotSurvivesUninstallTests(TestCase):
    """
    The property the whole base/Feature split rests on.

    An order priced by a Feature must stay readable once that Feature is gone.
    Simulated by deleting the promotion the order was priced from, which is what
    an uninstall eventually does to its tables.
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

        # The Feature is uninstalled and its rows go with it.
        promotion.delete()

        order.refresh_from_db()

        self.assertEqual(order.promotion.promotion_name, "Ramadan 20%")
        self.assertEqual(order.promotion.discount_amount, Decimal("20000"))
        self.assertEqual(order.total, Decimal("80000"))
