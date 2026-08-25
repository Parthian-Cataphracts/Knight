"""
The optional Feature, and the property that makes it optional.

This suite used to cover promotions and delivery, because both were Features.
Both are base-store capabilities now, and their tests moved with the code into
`apps.promotions` and `apps.fulfillment` — where they run unconditionally,
which is the point of the move
([`adr/0024`](../../../../../docs/adr/0024-base-store-versus-optional-feature.md)).

What is left here is the *seam*: `advanced-promotions` prices rules the base
store cannot, the store keeps working when it is absent, and an order priced by
it stays explicable once it is gone.

Skipped when the package is not installed, which is the honest thing for a suite
that must also pass on a base store with no Features on it.

"Installed" here means installed *as a Feature* — present in the store's feature
registry and therefore in `INSTALLED_APPS` — not merely importable. The two are
different states, and only the first one makes the models usable: a package that
pip has put on the path but the installer has never registered raises
`RuntimeError` from the model metaclass, not `ImportError`. Asking Django's app
registry is the only check that distinguishes them.
"""

import os
from decimal import Decimal
from unittest import skipUnless

from django.apps import apps as django_apps
from django.test import TestCase

from apps.promotions import services as promotions
from apps.promotions.models import DiscountType, Promotion, PromotionStatus
from apps.promotions.services import BasketLine

ADVANCED_INSTALLED = django_apps.is_installed("knight_feature_promotions")

# CI installs the Feature and must therefore run this suite. Skipping is the
# right behaviour on a base store and the wrong behaviour there, and the
# difference is invisible in a green run — the same reason the backend suite
# refuses to skip its PostgreSQL tests when REQUIRE_POSTGRES_TESTS is set
# ([`adr/0005`](../../../../../docs/adr/0005-postgresql-integration-testing.md)).
if os.environ.get("REQUIRE_FEATURE_TESTS") == "1" and not ADVANCED_INSTALLED:
    raise RuntimeError(
        "REQUIRE_FEATURE_TESTS=1 but advanced-promotions is not installed on this store. "
        "Register it with `manage.py knight_install_local` and pip install the package "
        "before running the suite; letting these tests skip here would report a pass for "
        "code nothing ran."
    )

if ADVANCED_INSTALLED:  # pragma: no cover - guarded by the registry above
    from knight_feature_promotions.models import Bundle, BundleItem, BuyXGetY, CampaignStatus


class TheStoreWorksWithoutTheFeatureTests(TestCase):
    """
    Runs whether or not the Feature is installed, and has to pass either way.

    That is the actual contract: base pricing does not change its answer because
    an optional package happens to be on the machine.
    """

    def test_base_pricing_answers_with_no_basket_lines_at_all(self):
        # Lines are what the advanced rules need. The base rules never did, and
        # a caller that does not pass them must still get priced.
        Promotion.objects.create(
            name="Ten off",
            status=PromotionStatus.ACTIVE,
            discount_type=DiscountType.FIXED_AMOUNT,
            discount_value=Decimal("10000"),
            requires_coupon=False,
        )

        outcome = promotions.price(Decimal("100000"))

        self.assertEqual(outcome.discount_amount, Decimal("10000"))

    def test_an_empty_basket_is_priced_without_reaching_for_a_rule(self):
        self.assertFalse(promotions.price(Decimal("0"), lines=[]).applies)


@skipUnless(ADVANCED_INSTALLED, "The advanced-promotions Feature is not installed.")
class AdvancedPricingTests(TestCase):
    def test_buy_two_get_one_free_discounts_only_the_reward(self):
        BuyXGetY.objects.create(
            name="Buy 2 get 1 free",
            status=CampaignStatus.ACTIVE,
            trigger_product_id=7,
            trigger_quantity=2,
            reward_product_id=7,
            reward_quantity=1,
        )

        outcome = promotions.price(
            Decimal("150000"), lines=[BasketLine(7, 3, Decimal("50000"))]
        )

        self.assertEqual(outcome.discount_amount, Decimal("50000"))

    def test_the_items_that_earned_the_reward_are_not_themselves_the_reward(self):
        # Otherwise "buy 2 get 1 free" discounts all three of a basket of three.
        BuyXGetY.objects.create(
            name="Buy 2 get 1 free",
            status=CampaignStatus.ACTIVE,
            trigger_product_id=7,
            trigger_quantity=2,
            reward_product_id=7,
            reward_quantity=1,
        )

        outcome = promotions.price(
            Decimal("100000"), lines=[BasketLine(7, 2, Decimal("50000"))]
        )

        self.assertFalse(outcome.applies)

    def test_a_reward_the_shopper_is_not_buying_is_not_discounted(self):
        # A campaign cannot add goods to a basket, only make what is there
        # cheaper — pricing an absent reward discounts an item that never ships.
        BuyXGetY.objects.create(
            name="Buy a coffee, get a cake",
            status=CampaignStatus.ACTIVE,
            trigger_product_id=1,
            trigger_quantity=1,
            reward_product_id=2,
            reward_quantity=1,
        )

        outcome = promotions.price(
            Decimal("50000"), lines=[BasketLine(1, 1, Decimal("50000"))]
        )

        self.assertFalse(outcome.applies)

    def test_awards_can_be_capped_per_order(self):
        BuyXGetY.objects.create(
            name="Buy 1 get 1, once",
            status=CampaignStatus.ACTIVE,
            trigger_product_id=1,
            trigger_quantity=1,
            reward_product_id=2,
            reward_quantity=1,
            maximum_awards_per_order=1,
        )

        outcome = promotions.price(
            Decimal("400000"),
            lines=[BasketLine(1, 4, Decimal("50000")), BasketLine(2, 4, Decimal("50000"))],
        )

        self.assertEqual(outcome.discount_amount, Decimal("50000"))

    def test_a_bundle_is_the_saving_against_list_price(self):
        bundle = Bundle.objects.create(
            name="Meal deal", status=CampaignStatus.ACTIVE, bundle_price=Decimal("90000")
        )
        BundleItem.objects.create(bundle=bundle, product_id=1, quantity=1)
        BundleItem.objects.create(bundle=bundle, product_id=2, quantity=1)

        outcome = promotions.price(
            Decimal("120000"),
            lines=[BasketLine(1, 1, Decimal("60000")), BasketLine(2, 1, Decimal("60000"))],
        )

        self.assertEqual(outcome.discount_amount, Decimal("30000"))

    def test_an_incomplete_bundle_is_not_a_partial_discount(self):
        bundle = Bundle.objects.create(
            name="Meal deal", status=CampaignStatus.ACTIVE, bundle_price=Decimal("90000")
        )
        BundleItem.objects.create(bundle=bundle, product_id=1, quantity=1)
        BundleItem.objects.create(bundle=bundle, product_id=2, quantity=1)

        outcome = promotions.price(
            Decimal("60000"), lines=[BasketLine(1, 1, Decimal("60000"))]
        )

        self.assertFalse(outcome.applies)

    def test_a_draft_campaign_does_not_price(self):
        BuyXGetY.objects.create(
            name="Not yet",
            status=CampaignStatus.DRAFT,
            trigger_product_id=1,
            trigger_quantity=1,
            reward_product_id=1,
            reward_quantity=1,
        )

        self.assertFalse(
            promotions.price(Decimal("100000"), lines=[BasketLine(1, 2, Decimal("50000"))]).applies
        )


@skipUnless(ADVANCED_INSTALLED, "The advanced-promotions Feature is not installed.")
class StackingTests(TestCase):
    def setUp(self):
        coded = Promotion.objects.create(
            name="Ramadan 20%",
            status=PromotionStatus.ACTIVE,
            discount_type=DiscountType.PERCENTAGE,
            discount_value=Decimal("20"),
            requires_coupon=True,
        )
        from apps.promotions.models import Coupon

        Coupon.objects.create(promotion=coded, code="RAMADAN20")

        self.bundle = Bundle.objects.create(
            name="Meal deal", status=CampaignStatus.ACTIVE, bundle_price=Decimal("90000")
        )
        BundleItem.objects.create(bundle=self.bundle, product_id=1, quantity=1)
        BundleItem.objects.create(bundle=self.bundle, product_id=2, quantity=1)

        self.lines = [BasketLine(1, 1, Decimal("60000")), BasketLine(2, 1, Decimal("60000"))]

    def test_by_default_the_better_of_the_two_wins_and_they_do_not_add(self):
        # Two rules applying in full is how a basket ends up discounted twice for
        # the same reason, so not stacking is the default.
        outcome = promotions.price(
            Decimal("120000"), coupon_code="RAMADAN20", lines=self.lines
        )

        self.assertEqual(outcome.discount_amount, Decimal("30000"))
        self.assertEqual(outcome.promotion_name, "Meal deal")

    def test_a_rule_that_declares_itself_stackable_adds_to_the_coupon(self):
        self.bundle.stacks = True
        self.bundle.save()

        outcome = promotions.price(
            Decimal("120000"), coupon_code="RAMADAN20", lines=self.lines
        )

        self.assertEqual(outcome.discount_amount, Decimal("54000"))
        self.assertIn("Ramadan", outcome.promotion_name)
        self.assertIn("Meal deal", outcome.promotion_name)

    def test_a_stacked_discount_never_exceeds_the_basket(self):
        # A discount larger than the goods is a refund, which is a different
        # transaction entirely.
        self.bundle.stacks = True
        self.bundle.bundle_price = Decimal("0")
        self.bundle.save()

        outcome = promotions.price(
            Decimal("120000"), coupon_code="RAMADAN20", lines=self.lines
        )

        self.assertEqual(outcome.discount_amount, Decimal("120000"))


@skipUnless(ADVANCED_INSTALLED, "The advanced-promotions Feature is not installed.")
class SnapshotSurvivesUninstallTests(TestCase):
    """
    An order priced by the Feature must stay readable once the Feature is gone.

    Simulated by deleting the campaign the order was priced from, which is what
    an uninstall eventually does to its tables.
    """

    def test_an_order_keeps_an_advanced_discount_after_the_campaign_is_deleted(self):
        from apps.orders.models import Order, OrderPromotion

        bundle = Bundle.objects.create(
            name="Meal deal", status=CampaignStatus.ACTIVE, bundle_price=Decimal("90000")
        )
        BundleItem.objects.create(bundle=bundle, product_id=1, quantity=1)
        BundleItem.objects.create(bundle=bundle, product_id=2, quantity=1)

        outcome = promotions.price(
            Decimal("120000"),
            lines=[BasketLine(1, 1, Decimal("60000")), BasketLine(2, 1, Decimal("60000"))],
        )

        order = Order.place(
            subtotal=Decimal("120000"),
            discount_total=outcome.discount_amount,
            total=Decimal("90000"),
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
        bundle.delete()

        order.refresh_from_db()

        self.assertEqual(order.promotion.promotion_name, "Meal deal")
        self.assertEqual(order.promotion.discount_amount, Decimal("30000"))
        self.assertEqual(order.total, Decimal("90000"))

    def test_the_snapshot_never_points_at_a_base_promotion_row(self):
        # `source_promotion_id` names a row in apps.promotions. An advanced rule
        # is not one, so recording the Feature's id there would make the snapshot
        # point at the wrong table the moment anybody trusted it.
        bundle = Bundle.objects.create(
            name="Meal deal", status=CampaignStatus.ACTIVE, bundle_price=Decimal("90000")
        )
        BundleItem.objects.create(bundle=bundle, product_id=1, quantity=1)

        outcome = promotions.price(
            Decimal("120000"), lines=[BasketLine(1, 1, Decimal("120000"))]
        )

        self.assertTrue(outcome.applies)
        self.assertIsNone(outcome.promotion_id)
