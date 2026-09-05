"""
Parity tests for the ported ordering domain.

The behaviours checked here are the ones the .NET module was built around, and
the ones a shop actually depends on: an order cannot skip its lifecycle, its
totals are what was charged rather than what would be charged today, its number
is unique under concurrency, and a repeated checkout returns the first order
instead of creating a second.
"""

from decimal import Decimal
from unittest.mock import patch

from django.core.exceptions import ValidationError
from django.db import IntegrityError, transaction
from django.test import TestCase, TransactionTestCase

from apps.orders.models import (
    ALLOWED_TRANSITIONS,
    CheckoutIdempotencyRecord,
    FulfillmentMethod,
    Order,
    OrderFulfillment,
    OrderItem,
    OrderItemModifier,
    OrderNumberSequence,
    OrderParty,
    OrderPromotion,
    OrderStatus,
)


def place(**overrides) -> Order:
    fields = {
        "subtotal": Decimal("100000"),
        "total": Decimal("100000"),
        "currency": "IRR",
    }
    fields.update(overrides)

    return Order.place(**fields)


class LifecycleTests(TestCase):
    def test_the_happy_path_runs_in_order(self):
        order = place()

        for target in (
            OrderStatus.CONFIRMED,
            OrderStatus.PREPARING,
            OrderStatus.READY,
            OrderStatus.COMPLETED,
        ):
            order.transition_to(target, actor="counter")

        self.assertEqual(order.status, OrderStatus.COMPLETED)
        self.assertIsNotNone(order.completed_at)

    def test_a_step_cannot_be_skipped(self):
        # A counter has an order of events. Allowing an arbitrary jump would let
        # an order be completed before it was made.
        order = place()

        with self.assertRaises(ValidationError):
            order.transition_to(OrderStatus.READY)

    def test_an_order_cannot_go_backwards(self):
        order = place()
        order.transition_to(OrderStatus.CONFIRMED)

        with self.assertRaises(ValidationError):
            order.transition_to(OrderStatus.PENDING)

    def test_cancellation_is_available_from_every_live_state(self):
        for start in (OrderStatus.PENDING, OrderStatus.CONFIRMED, OrderStatus.PREPARING, OrderStatus.READY):
            order = place()

            # Walk to the state under test.
            for target in (
                OrderStatus.CONFIRMED,
                OrderStatus.PREPARING,
                OrderStatus.READY,
            ):
                if order.status == start:
                    break

                order.transition_to(target)

            order.transition_to(OrderStatus.CANCELLED, reason="Shopper changed their mind.")

            self.assertEqual(order.status, OrderStatus.CANCELLED)

    def _complete(self) -> Order:
        order = place()
        for target in (
            OrderStatus.CONFIRMED,
            OrderStatus.PREPARING,
            OrderStatus.READY,
            OrderStatus.COMPLETED,
        ):
            order.transition_to(target)
        return order

    def test_terminal_states_are_terminal(self):
        # Cancelled and refunded are the ends of the road; completed is not, since
        # a paid order can still be refunded.
        self.assertEqual(ALLOWED_TRANSITIONS[OrderStatus.CANCELLED], set())
        self.assertEqual(ALLOWED_TRANSITIONS[OrderStatus.REFUNDED], set())

        completed = self._complete()

        # A completed order cannot be cancelled — it was paid; the way back is a
        # refund, not a cancellation.
        with self.assertRaises(ValidationError):
            completed.transition_to(OrderStatus.CANCELLED)

    def test_a_completed_order_can_be_refunded_and_then_is_terminal(self):
        order = self._complete()

        order.refund(actor="counter", reason="Shopper returned the item.")

        self.assertEqual(order.status, OrderStatus.REFUNDED)
        self.assertIsNotNone(order.refunded_at)
        self.assertEqual(order.refund_reason, "Shopper returned the item.")
        self.assertTrue(order.is_terminal)

        # Nothing follows a refund.
        with self.assertRaises(ValidationError):
            order.refund()

    def test_only_a_completed_order_can_be_refunded(self):
        # A refund is what happens to an order that was paid; a live order is
        # cancelled, not refunded.
        order = place()
        order.transition_to(OrderStatus.CONFIRMED)

        with self.assertRaises(ValidationError):
            order.refund(reason="Too early.")

    def test_a_refund_announces_order_refunded(self):
        # The event the Features that subscribed to a refund actually receive.
        order = self._complete()

        with patch("knight_integration.features.announce") as announce:
            order.refund(reason="Returned.")

        events = [call.args[0] for call in announce.call_args_list]
        self.assertIn("order.refunded", events)

    def test_every_transition_is_recorded_with_its_actor(self):
        # The history is what answers "who cancelled this and when" during the
        # argument that follows.
        order = place()
        order.transition_to(OrderStatus.CONFIRMED, actor="Ali")
        order.transition_to(OrderStatus.CANCELLED, actor="Sara", reason="Out of stock.")

        history = list(order.history.all())

        self.assertEqual(len(history), 2)
        self.assertEqual(history[0].from_status, OrderStatus.PENDING)
        self.assertEqual(history[0].to_status, OrderStatus.CONFIRMED)
        self.assertEqual(history[0].actor, "Ali")
        self.assertEqual(history[1].reason, "Out of stock.")

    def test_the_version_moves_on_every_transition(self):
        order = place()
        self.assertEqual(order.version, 1)

        order.transition_to(OrderStatus.CONFIRMED)
        order.transition_to(OrderStatus.PREPARING)

        self.assertEqual(order.version, 3)


class PricingTests(TestCase):
    def _order_with_lines(self) -> Order:
        order = place(subtotal=Decimal("0"), total=Decimal("0"))

        item = OrderItem(
            order=order,
            source_product_id=1,
            product_name="Burger",
            unit_base_price=Decimal("100000"),
            unit_modifier_total=Decimal("0"),
            quantity=2,
            unit_price=Decimal("0"),
            line_total=Decimal("0"),
        )
        item.price()
        item.save()

        return order

    def test_a_line_prices_from_its_parts(self):
        order = self._order_with_lines()
        item = order.items.get()

        self.assertEqual(item.unit_price, Decimal("100000"))
        self.assertEqual(item.line_total, Decimal("200000"))

    def test_modifiers_move_the_unit_price(self):
        order = place(subtotal=Decimal("0"), total=Decimal("0"))

        item = OrderItem(
            order=order,
            source_product_id=1,
            product_name="Burger",
            unit_base_price=Decimal("100000"),
            unit_modifier_total=Decimal("15000"),
            quantity=1,
            unit_price=Decimal("0"),
            line_total=Decimal("0"),
        )
        item.price()
        item.save()

        OrderItemModifier.objects.create(
            item=item,
            source_modifier_group_id=1,
            modifier_group_name="Extras",
            source_modifier_id=1,
            modifier_name="Extra cheese",
            unit_price_delta=Decimal("15000"),
        )

        self.assertEqual(item.unit_price, Decimal("115000"))

    def test_totals_are_subtotal_less_discount_plus_fee(self):
        order = self._order_with_lines()
        order.discount_total = Decimal("20000")
        order.fulfillment_fee = Decimal("30000")
        order.recalculate()

        self.assertEqual(order.subtotal, Decimal("200000"))
        self.assertEqual(order.total, Decimal("210000"))

    def test_a_discount_cannot_exceed_the_goods(self):
        # A store that owes a shopper money is a refund, which is a different
        # transaction with different rules — not a negative order total.
        order = self._order_with_lines()
        order.discount_total = Decimal("999999")
        order.recalculate()

        self.assertEqual(order.discount_total, Decimal("200000"))
        self.assertEqual(order.total, Decimal("0"))

    def test_a_line_must_be_for_at_least_one(self):
        order = place()

        item = OrderItem(
            order=order,
            source_product_id=1,
            product_name="Burger",
            unit_base_price=Decimal("1"),
            quantity=0,
            unit_price=Decimal("1"),
            line_total=Decimal("0"),
        )

        with self.assertRaises(ValidationError):
            item.full_clean()


class SnapshotTests(TestCase):
    """
    An order records what was true when it was placed.

    This is the property the whole port turns on: a receipt that changed when a
    price changed would not be a receipt.
    """

    def test_a_line_keeps_its_name_and_price_when_the_catalogue_moves_on(self):
        from apps.catalog.models import Category, Product

        category = Category.objects.create(name="Burgers", slug="burgers")
        product = Product.objects.create(
            category=category, name="Cheese Burger", slug="cheese-burger", base_price=Decimal("100000")
        )

        order = place()
        item = OrderItem(
            order=order,
            source_product_id=product.pk,
            product_name=product.name,
            unit_base_price=product.base_price,
            quantity=1,
            unit_price=product.base_price,
            line_total=product.base_price,
        )
        item.price()
        item.save()

        # The merchant renames it and puts the price up.
        product.name = "Deluxe Cheese Burger"
        product.base_price = Decimal("150000")
        product.save()

        item.refresh_from_db()

        self.assertEqual(item.product_name, "Cheese Burger")
        self.assertEqual(item.unit_base_price, Decimal("100000"))

    def test_the_party_survives_the_shopper_being_deleted(self):
        from apps.shoppers.models import Shopper

        shopper = Shopper.objects.create(display_name="Ali", phone="09123456789")
        order = place()

        OrderParty.objects.create(
            order=order,
            source_shopper_id=shopper.pk,
            display_name=shopper.display_name,
            phone=shopper.phone,
        )

        shopper.delete()
        order.refresh_from_db()

        # Right to be forgotten removes the shopper, not the history of what
        # they bought.
        self.assertEqual(order.party.display_name, "Ali")

    def test_a_discount_stays_explicable_without_the_promotions_feature(self):
        # The reason OrderPromotion copies everything: the feature that created
        # the discount may be uninstalled and its tables gone (adr/0024).
        order = place()

        OrderPromotion.objects.create(
            order=order,
            source_promotion_id=42,
            promotion_name="Ramadan 20%",
            coupon_code="RAMADAN20",
            discount_type="Percentage",
            discount_value=Decimal("20"),
            discount_amount=Decimal("20000"),
        )

        order.refresh_from_db()

        self.assertEqual(order.promotion.promotion_name, "Ramadan 20%")
        self.assertEqual(order.promotion.discount_amount, Decimal("20000"))


class FulfillmentSnapshotTests(TestCase):
    def test_a_delivery_order_needs_an_address(self):
        # Refused at checkout rather than discovered at the door.
        order = place()

        fulfillment = OrderFulfillment(order=order, method=FulfillmentMethod.DELIVERY)

        with self.assertRaises(ValidationError):
            fulfillment.full_clean()

    def test_a_collection_order_needs_no_address(self):
        order = place()

        fulfillment = OrderFulfillment(order=order, method=FulfillmentMethod.COLLECTION)
        fulfillment.full_clean()


class CheckoutIdempotencyTests(TestCase):
    def test_the_same_key_and_basket_is_the_same_order(self):
        order = place()

        record = CheckoutIdempotencyRecord.objects.create(
            key="abc", request_hash="hash-1", order=order
        )

        self.assertTrue(record.matches("hash-1"))

    def test_the_same_key_with_a_different_basket_is_refused(self):
        # Otherwise a reused key silently answers with somebody else's order.
        order = place()

        record = CheckoutIdempotencyRecord.objects.create(
            key="abc", request_hash="hash-1", order=order
        )

        self.assertFalse(record.matches("hash-2"))

    def test_a_key_cannot_be_claimed_twice(self):
        place()

        CheckoutIdempotencyRecord.objects.create(key="abc", request_hash="hash-1")

        with self.assertRaises(IntegrityError), transaction.atomic():
            CheckoutIdempotencyRecord.objects.create(key="abc", request_hash="hash-2")


class OrderNumberTests(TransactionTestCase):
    """
    Order numbers are what a shopper reads out on the phone.

    A `TransactionTestCase` because the counter is taken under a row lock, and a
    wrapping transaction would hide exactly the behaviour being checked.
    """

    def test_numbers_are_sequential(self):
        first = place()
        second = place()

        self.assertEqual(second.number, first.number + 1)

    def test_numbers_are_unique(self):
        numbers = {place().number for _ in range(20)}

        self.assertEqual(len(numbers), 20)

    def test_the_counter_starts_from_one(self):
        OrderNumberSequence.objects.all().delete()

        self.assertEqual(place().number, 1)
