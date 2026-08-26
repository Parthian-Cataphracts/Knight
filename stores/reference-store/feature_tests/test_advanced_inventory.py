"""
`advanced-inventory`, installed.

What is worth pinning here is not "stock goes up and down". It is the four
things that make an inventory Feature either trustworthy or the source of every
argument in the shop:

- the quantity is **derived from the ledger** and there is no counter to drift;
- **available is not on hand** — it is on hand minus what is held, and selling
  against the wrong one of those is how the last item gets sold twice;
- a **hold always ends**, whether or not a scheduled job ran;
- and the reservation path is right **under concurrency**, which is the one
  claim in this repository that has been argued since phase 14 and never
  demonstrated. `ConcurrentReservationTests` is that demonstration.
"""

import threading
from datetime import timedelta
from decimal import Decimal
from unittest import skipUnless

from django.db import connection
from django.test import TestCase, TransactionTestCase
from django.utils import timezone

from feature_tests.support import installed, require

APP = "knight_feature_advanced_inventory"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_advanced_inventory import services
    from knight_feature_advanced_inventory.models import (
        AlertKind,
        PurchaseOrderState,
        Reservation,
        ReservationState,
        StockAlert,
        StockItem,
        StockMovement,
    )


def _d(value) -> Decimal:
    return Decimal(str(value)).quantize(Decimal("0.000"))


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class LedgerTests(TestCase):
    """The rule the whole package rests on: movements are the truth."""

    def setUp(self):
        services.define_item("ESP-01", name="Espresso beans", unit="kg")

    def test_stock_is_the_sum_of_its_movements(self):
        services.receive("ESP-01", 10)
        services.sell("ESP-01", 3)
        services.take_back("ESP-01", 1)

        self.assertEqual(_d(8), services.on_hand("ESP-01"))

    def test_there_is_no_quantity_column_to_drift(self):
        # Stated as a test because it is the design, and a later "just cache it
        # on the item" is exactly the change this is here to stop.
        self.assertNotIn("quantity", [field.name for field in StockItem._meta.get_fields()])

    def test_the_sign_comes_from_the_reason_not_from_the_caller(self):
        # A call site that passed a positive number for a sale would otherwise
        # double the stock of the thing it just sold, and that mistake is one
        # line of application code away everywhere.
        services.receive("ESP-01", 10)
        services.sell("ESP-01", 4)

        self.assertEqual(_d(6), services.on_hand("ESP-01"))
        self.assertEqual(_d(-4), StockMovement.objects.get(reason="sale").quantity)

    def test_a_refund_writes_a_row_rather_than_undoing_one(self):
        services.receive("ESP-01", 5)
        services.sell("ESP-01", 2, reference="order-1")
        services.take_back("ESP-01", 2, reference="order-1")

        # The sale happened. A ledger that edited it away would answer "how much
        # did we sell in March" with a number that changes on every return.
        self.assertEqual(1, StockMovement.objects.filter(reason="sale").count())
        self.assertEqual(1, StockMovement.objects.filter(reason="return").count())
        self.assertEqual(_d(5), services.on_hand("ESP-01"))

    def test_stock_may_go_negative_and_says_so(self):
        # A shop whose books say -3 has a counting problem, and rounding it to
        # zero here would hide the one number that says so.
        services.sell("ESP-01", 3)

        self.assertEqual(_d(-3), services.on_hand("ESP-01"))
        self.assertEqual(_d(0), services.available("ESP-01"))

    def test_a_movement_of_zero_is_refused(self):
        with self.assertRaises(services.InventoryError):
            services.receive("ESP-01", 0)

    def test_quantities_keep_three_decimal_places(self):
        # A shop selling by weight counts in fractions, and an integer column
        # would have been a migration nobody wants to write.
        services.receive("ESP-01", "1.250")
        services.sell("ESP-01", "0.750")

        self.assertEqual(_d("0.500"), services.on_hand("ESP-01"))

    def test_an_unknown_sku_is_named_rather_than_created(self):
        with self.assertRaises(services.UnknownItem):
            services.receive("NOT-A-SKU", 1)

    def test_skus_are_matched_however_they_were_typed(self):
        services.receive("esp-01", 4)

        self.assertEqual(_d(4), services.on_hand("ESP-01"))

    def test_history_answers_why_the_number_is_what_it_is(self):
        services.receive("ESP-01", 10, reference="po-1")
        services.sell("ESP-01", 1, reference="order-9")

        reasons = [movement.reason for movement in services.history("ESP-01")]

        self.assertEqual(["sale", "receipt"], reasons)


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class StocktakeTests(TestCase):
    def setUp(self):
        services.define_item("ESP-01", name="Espresso beans")
        services.receive("ESP-01", 10)

    def test_a_count_records_the_difference_not_the_total(self):
        services.count("ESP-01", 8)

        self.assertEqual(_d(8), services.on_hand("ESP-01"))
        self.assertEqual(_d(-2), StockMovement.objects.get(reason="adjustment").quantity)

    def test_a_count_that_agrees_writes_nothing(self):
        # A movement of zero would sit in the history implying something
        # happened, and would make "when did this last move" wrong.
        self.assertIsNone(services.count("ESP-01", 10))
        self.assertEqual(0, StockMovement.objects.filter(reason="adjustment").count())

    def test_a_count_can_correct_upwards(self):
        services.count("ESP-01", 12)

        self.assertEqual(_d(12), services.on_hand("ESP-01"))


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class TransferTests(TestCase):
    def setUp(self):
        services.define_item("ESP-01", name="Espresso beans")
        services.receive("ESP-01", 10, location="main")

    def test_a_transfer_is_two_rows_and_both_locations_add_up(self):
        services.transfer("ESP-01", 4, source="main", destination="kiosk")

        self.assertEqual(_d(6), services.on_hand("ESP-01", location="main"))
        self.assertEqual(_d(4), services.on_hand("ESP-01", location="kiosk"))

    def test_the_total_across_locations_is_unchanged(self):
        services.transfer("ESP-01", 4, source="main", destination="kiosk")

        self.assertEqual(_d(10), services.on_hand("ESP-01"))

    def test_a_transfer_to_the_same_place_is_refused(self):
        with self.assertRaises(services.InventoryError):
            services.transfer("ESP-01", 1, source="main", destination="main")


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class ReservationTests(TestCase):
    def setUp(self):
        services.define_item("ESP-01", name="Espresso beans")
        services.receive("ESP-01", 5)

    def test_a_hold_reduces_what_may_be_sold_without_moving_anything(self):
        services.reserve("ESP-01", 2, reference="basket-1")

        self.assertEqual(_d(5), services.on_hand("ESP-01"))
        self.assertEqual(_d(3), services.available("ESP-01"))
        self.assertEqual(0, StockMovement.objects.filter(reason="sale").count())

    def test_a_hold_larger_than_what_is_available_is_refused_with_the_numbers(self):
        # A caller showing "out of stock" and a caller showing "only 2 left"
        # need the same refusal and different words.
        services.reserve("ESP-01", 4, reference="basket-1")

        with self.assertRaises(services.NotEnoughStock) as raised:
            services.reserve("ESP-01", 2, reference="basket-2")

        self.assertEqual(_d(2), raised.exception.requested)
        self.assertEqual(_d(1), raised.exception.available)

    def test_reserving_twice_for_one_order_returns_the_same_hold(self):
        # A checkout retried after a timeout must not hold the stock twice.
        first = services.reserve("ESP-01", 2, reference="basket-1")
        second = services.reserve("ESP-01", 2, reference="basket-1")

        self.assertEqual(first.pk, second.pk)
        self.assertEqual(_d(3), services.available("ESP-01"))

    def test_committing_writes_the_sale_and_ends_the_hold(self):
        services.reserve("ESP-01", 2, reference="basket-1")
        services.commit("basket-1")

        self.assertEqual(_d(3), services.on_hand("ESP-01"))
        self.assertEqual(_d(3), services.available("ESP-01"))
        self.assertEqual(ReservationState.COMMITTED, Reservation.objects.get().state)

    def test_committing_twice_does_not_sell_it_twice(self):
        # A payment webhook delivered twice is the ordinary case, not the
        # exotic one.
        services.reserve("ESP-01", 2, reference="basket-1")
        services.commit("basket-1")
        services.commit("basket-1")

        self.assertEqual(1, StockMovement.objects.filter(reason="sale").count())
        self.assertEqual(_d(3), services.on_hand("ESP-01"))

    def test_releasing_gives_the_stock_back_and_moves_nothing(self):
        services.reserve("ESP-01", 2, reference="basket-1")
        services.release("basket-1")

        self.assertEqual(_d(5), services.available("ESP-01"))
        self.assertEqual(0, StockMovement.objects.count() - 1)  # the receipt only

    def test_an_expired_hold_stops_counting_before_any_job_runs(self):
        # The important half. An expiry that only took effect when the worker
        # ran would put the arithmetic at the mercy of a crontab.
        services.reserve("ESP-01", 5, reference="basket-1", minutes=1)

        later = timezone.now() + timedelta(minutes=2)

        self.assertEqual(_d(0), services.available("ESP-01"))
        self.assertEqual(_d(5), services.available("ESP-01", now=later))

    def test_the_worker_tidies_expired_holds_away(self):
        services.reserve("ESP-01", 5, reference="basket-1", minutes=1)

        expired = services.expire_reservations(now=timezone.now() + timedelta(minutes=2))

        self.assertEqual(1, expired)
        self.assertEqual(ReservationState.EXPIRED, Reservation.objects.get().state)

    def test_reusing_a_settled_reference_is_named_rather_than_absorbed(self):
        services.reserve("ESP-01", 1, reference="basket-1")
        services.commit("basket-1")

        with self.assertRaises(services.InventoryError):
            services.reserve("ESP-01", 1, reference="basket-1")

    def test_a_hold_needs_something_to_hold_it_for(self):
        with self.assertRaises(services.InventoryError):
            services.reserve("ESP-01", 1, reference="   ")


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class ConcurrentReservationTests(TransactionTestCase):
    """
    Two shoppers, one item, at the same moment.

    This is the test phase 14 and phase 15 both carried forward as "concurrency
    is argued, not proven". The argument was that `reserve` takes a row lock
    before it reads, so two callers cannot both see the last item as free. This
    runs two real connections at it and checks.

    A `TransactionTestCase` rather than a `TestCase` because the threads need to
    see committed data and each other's locks, which a test wrapped in one
    transaction cannot provide.
    """

    def _attempt(self, reference, barrier, outcomes):
        def run():
            try:
                barrier.wait(timeout=10)
                services.reserve("ESP-01", 1, reference=reference)
                outcomes.append("held")
            except services.NotEnoughStock:
                outcomes.append("refused")
            except Exception as exc:  # noqa: BLE001 - reported, not swallowed
                outcomes.append(f"{type(exc).__name__}: {exc}")
            finally:
                # Each thread has its own connection and has to close it, or the
                # test database cannot be torn down.
                connection.close()

        return threading.Thread(target=run)

    def test_two_reservers_of_the_last_item_cannot_both_win(self):
        services.define_item("ESP-01", name="Espresso beans")
        services.receive("ESP-01", 1)

        barrier = threading.Barrier(2)
        outcomes: list[str] = []

        threads = [
            self._attempt("basket-1", barrier, outcomes),
            self._attempt("basket-2", barrier, outcomes),
        ]

        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=20)

        self.assertEqual(["held", "refused"], sorted(outcomes))
        self.assertEqual(1, Reservation.objects.filter(state=ReservationState.HELD).count())
        self.assertEqual(_d(0), services.available("ESP-01"))

    def test_two_reservers_of_a_shelf_with_enough_on_it_both_win(self):
        # The other half, and the reason the lock is on the item row rather than
        # on the table: contention must cost a wait, not a refusal.
        services.define_item("ESP-01", name="Espresso beans")
        services.receive("ESP-01", 2)

        barrier = threading.Barrier(2)
        outcomes: list[str] = []

        threads = [
            self._attempt("basket-1", barrier, outcomes),
            self._attempt("basket-2", barrier, outcomes),
        ]

        for thread in threads:
            thread.start()
        for thread in threads:
            thread.join(timeout=20)

        self.assertEqual(["held", "held"], sorted(outcomes))
        self.assertEqual(_d(0), services.available("ESP-01"))


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class AlertTests(TestCase):
    def setUp(self):
        services.define_item("ESP-01", name="Espresso beans", reorder_point=5, reorder_quantity=20)

    def test_the_sweep_raises_an_alert_only_for_what_is_low(self):
        services.define_item("CUP-01", name="Cups", reorder_point=100, reorder_quantity=500)
        services.receive("ESP-01", 3)
        services.receive("CUP-01", 400)

        counts = services.sweep_low_stock()

        self.assertEqual(1, counts["raised"])
        self.assertEqual(["ESP-01"], [alert.item.sku for alert in services.open_alerts()])

    def test_running_it_again_does_not_raise_a_second_copy(self):
        # A daily sweep that raised a fresh alert every morning would bury the
        # item that has been out for a week under thirty copies of itself.
        services.receive("ESP-01", 3)
        services.sweep_low_stock()
        counts = services.sweep_low_stock()

        self.assertEqual(0, counts["raised"])
        self.assertEqual(1, StockAlert.objects.filter(resolved_at__isnull=True).count())

    def test_restocking_resolves_the_alert(self):
        # A list that still shows what was restocked last week is a list nobody
        # reads.
        services.receive("ESP-01", 3)
        services.sweep_low_stock()
        services.receive("ESP-01", 50)

        counts = services.sweep_low_stock()

        self.assertEqual(1, counts["resolved"])
        self.assertEqual([], services.open_alerts())

    def test_low_becoming_out_is_raised_again_as_worse_news(self):
        services.receive("ESP-01", 3)
        services.sweep_low_stock()
        services.sell("ESP-01", 3)
        services.sweep_low_stock()

        alert = StockAlert.objects.filter(resolved_at__isnull=True).get()

        self.assertEqual(AlertKind.OUT, alert.kind)

    def test_held_stock_counts_against_the_threshold(self):
        # The alert is about what the shop can sell, not what is on the shelf.
        services.receive("ESP-01", 6)
        services.reserve("ESP-01", 4, reference="basket-1")

        services.sweep_low_stock()

        self.assertEqual(["ESP-01"], [alert.item.sku for alert in services.open_alerts()])

    def test_an_item_with_no_reorder_point_is_not_alerted_on(self):
        # Nobody has said what "low" means for it, so nobody asked to be told.
        # Alerting on it anyway fills the list with things that were never being
        # watched, which is how a merchant learns to ignore the list.
        services.define_item("MISC-01", name="Something nobody set a point for")

        services.sweep_low_stock()

        self.assertNotIn("MISC-01", [alert.item.sku for alert in services.open_alerts()])

    def test_an_untracked_item_is_left_alone(self):
        services.define_item("SVC-01", name="Barista training", reorder_point=5, is_tracked=False)

        services.sweep_low_stock()

        self.assertNotIn("SVC-01", [alert.item.sku for alert in services.open_alerts()])


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class ReorderTests(TestCase):
    def setUp(self):
        services.define_supplier("BEANCO", name="Bean Company")
        services.define_item(
            "ESP-01",
            name="Espresso beans",
            supplier_code="BEANCO",
            reorder_point=5,
            reorder_quantity=20,
        )
        services.receive("ESP-01", 2)

    def test_what_is_low_is_suggested_with_its_supplier(self):
        suggestion = services.reorder_suggestions()[0]

        self.assertEqual("ESP-01", suggestion.sku)
        self.assertEqual("BEANCO", suggestion.supplier_code)
        self.assertEqual(_d(20), suggestion.suggested_quantity)

    def test_a_suggestion_says_what_is_already_on_its_way(self):
        # A reorder list that ignored outstanding orders would suggest the same
        # thing every morning until the delivery arrived, and a merchant
        # following it would end up with five of them.
        services.create_purchase_order("PO-1", supplier_code="BEANCO")
        services.add_line("PO-1", "ESP-01", 20)
        services.place("PO-1")

        self.assertEqual(_d(20), services.reorder_suggestions()[0].on_order)

    def test_a_suggestion_with_no_quantity_says_so_rather_than_inventing_one(self):
        services.define_item("CUP-01", name="Cups", reorder_point=10, reorder_quantity=0)
        services.receive("CUP-01", 1)

        cups = [entry for entry in services.reorder_suggestions() if entry.sku == "CUP-01"][0]

        self.assertFalse(cups.has_a_quantity)

    def test_what_is_stocked_is_not_suggested(self):
        services.receive("ESP-01", 100)

        self.assertEqual([], services.reorder_suggestions())


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class PurchaseOrderTests(TestCase):
    def setUp(self):
        services.define_supplier("BEANCO", name="Bean Company")
        services.define_item("ESP-01", name="Espresso beans", supplier_code="BEANCO")
        services.create_purchase_order("PO-1", supplier_code="BEANCO")
        services.add_line("PO-1", "ESP-01", 10, unit_cost="7.50")

    def test_an_order_with_no_lines_is_not_sent(self):
        services.create_purchase_order("PO-EMPTY", supplier_code="BEANCO")

        with self.assertRaises(services.InventoryError):
            services.place("PO-EMPTY")

    def test_a_placed_order_cannot_be_edited(self):
        # Changing what was ordered after it was sent means the document in the
        # supplier's hands and the one in the shop disagree.
        services.place("PO-1")

        with self.assertRaises(services.InventoryError):
            services.add_line("PO-1", "ESP-01", 20)

    def test_receiving_moves_the_stock_in_the_same_act(self):
        services.place("PO-1")
        services.receive_line("PO-1", "ESP-01", 10)

        self.assertEqual(_d(10), services.on_hand("ESP-01"))

    def test_a_part_delivery_leaves_the_order_partially_received(self):
        services.place("PO-1")
        services.receive_line("PO-1", "ESP-01", 4)

        order = services.outstanding_orders()[0]

        self.assertEqual(PurchaseOrderState.PARTIAL, order.state)
        self.assertEqual(_d(6), order.lines.get().outstanding)

    def test_receiving_everything_closes_the_order(self):
        services.place("PO-1")
        services.receive_line("PO-1", "ESP-01", 6)
        services.receive_line("PO-1", "ESP-01", 4)

        self.assertEqual([], services.outstanding_orders())

    def test_receiving_more_than_was_ordered_is_refused(self):
        # A delivery larger than the order is either somebody else's stock or a
        # typo, and both are worth stopping to look at.
        services.place("PO-1")

        with self.assertRaises(services.InventoryError):
            services.receive_line("PO-1", "ESP-01", 11)

        self.assertEqual(_d(0), services.on_hand("ESP-01"))

    def test_an_order_with_stock_against_it_cannot_be_cancelled(self):
        services.place("PO-1")
        services.receive_line("PO-1", "ESP-01", 4)

        with self.assertRaises(services.InventoryError):
            services.cancel_purchase_order("PO-1")

    def test_an_untouched_order_can_be_cancelled(self):
        services.place("PO-1")

        self.assertEqual(PurchaseOrderState.CANCELLED, services.cancel_purchase_order("PO-1").state)

    def test_two_orders_cannot_share_a_reference(self):
        with self.assertRaises(services.InventoryError):
            services.create_purchase_order("PO-1", supplier_code="BEANCO")


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class LookupTests(TestCase):
    """
    The stock picker, which is what `pg_trgm` is here for.

    The person typing has a delivery in their hands and half a product name in
    their head, so an exact match must win and a near miss must still find
    something (docs/adr/0031-database-extensions-are-declared-not-migrated.md).
    """

    def setUp(self):
        services.define_supplier("BEANCO", name="Bean Company Limited")
        services.define_item("ESP-01", name="Espresso beans")
        services.define_item("ESP-02", name="Espresso beans, decaffeinated")
        services.define_item("CUP-01", name="Takeaway cups")

    def test_an_exact_sku_comes_first(self):
        # Somebody who typed a whole SKU knows what they want, and a similarity
        # ranking that put something else above it would be actively unhelpful.
        self.assertEqual("ESP-01", services.find_items("ESP-01")[0].sku)

    def test_a_misspelled_name_still_finds_the_item(self):
        self.assertIn("ESP-01", [item.sku for item in services.find_items("expresso beans")])

    def test_a_partial_name_finds_both_matching_items(self):
        found = {item.sku for item in services.find_items("espresso")}

        self.assertEqual({"ESP-01", "ESP-02"}, found)

    def test_a_misspelled_supplier_is_still_found(self):
        self.assertEqual(
            ["BEANCO"],
            [supplier.code for supplier in services.find_suppliers("bean compny")],
        )

    def test_an_empty_query_finds_nothing_rather_than_everything(self):
        self.assertEqual([], services.find_items(""))
        self.assertEqual([], services.find_suppliers("  "))


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class DeliveredRoutesTests(TestCase):
    """The routes the manifest declares, mounted where it says."""

    def setUp(self):
        services.define_item("ESP-01", name="Espresso beans", reorder_point=5, reorder_quantity=20)
        services.receive("ESP-01", 3)

    def test_the_levels_route_is_mounted_under_the_declared_prefix(self):
        response = self.client.get("/inventory/")

        self.assertEqual(200, response.status_code)
        self.assertEqual("ESP-01", response.json()["items"][0]["sku"])

    def test_availability_reports_all_three_numbers(self):
        services.reserve("ESP-01", 1, reference="basket-1")

        body = self.client.get("/inventory/ESP-01/").json()

        self.assertEqual("3.000", body["onHand"])
        self.assertEqual("1.000", body["held"])
        self.assertEqual("2.000", body["available"])

    def test_an_untracked_sku_answers_that_rather_than_failing(self):
        response = self.client.get("/inventory/NOT-A-SKU/")

        self.assertEqual(404, response.status_code)
        self.assertFalse(response.json()["tracked"])

    def test_the_history_route_answers(self):
        body = self.client.get("/inventory/ESP-01/history/").json()

        self.assertEqual("receipt", body["movements"][0]["reason"])

    def test_the_alerts_route_answers(self):
        services.sweep_low_stock()

        self.assertEqual("ESP-01", self.client.get("/inventory/alerts/").json()["alerts"][0]["sku"])

    def test_the_reorder_route_answers(self):
        body = self.client.get("/inventory/reorder/").json()

        self.assertEqual("ESP-01", body["suggestions"][0]["sku"])

    def test_the_search_route_is_typo_tolerant(self):
        body = self.client.get("/inventory/search/?q=expresso").json()

        self.assertEqual("ESP-01", body["results"][0]["sku"])

    def test_a_stocktake_posted_over_http_corrects_the_ledger(self):
        response = self.client.post(
            "/inventory/stocktake/",
            data={"sku": "ESP-01", "counted": "1"},
            content_type="application/json",
        )

        self.assertTrue(response.json()["corrected"])
        self.assertEqual(_d(1), services.on_hand("ESP-01"))

    def test_a_stocktake_that_agrees_reports_no_correction(self):
        response = self.client.post(
            "/inventory/stocktake/",
            data={"sku": "ESP-01", "counted": "3"},
            content_type="application/json",
        )

        self.assertFalse(response.json()["corrected"])

    def test_a_stocktake_for_an_unknown_sku_is_a_404_not_a_500(self):
        response = self.client.post(
            "/inventory/stocktake/",
            data={"sku": "NOPE", "counted": "1"},
            content_type="application/json",
        )

        self.assertEqual(404, response.status_code)

    def test_receiving_over_http_refuses_more_than_was_ordered(self):
        services.define_supplier("BEANCO", name="Bean Company")
        services.create_purchase_order("PO-1", supplier_code="BEANCO")
        services.add_line("PO-1", "ESP-01", 5)
        services.place("PO-1")

        response = self.client.post(
            "/inventory/receive/",
            data={"reference": "PO-1", "sku": "ESP-01", "quantity": "50"},
            content_type="application/json",
        )

        self.assertEqual(400, response.status_code)
        self.assertEqual(_d(3), services.on_hand("ESP-01"))


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class WorkerTests(TestCase):
    """The two entrypoints the manifest declares, called the way the runner calls them."""

    def test_both_entrypoints_take_no_arguments(self):
        services.define_item("ESP-01", name="Espresso beans", reorder_point=5)
        services.receive("ESP-01", 1)

        self.assertIn("expired", services.run_expiry())
        self.assertEqual(1, services.run_low_stock_sweep()["raised"])

    def test_the_expiry_worker_is_safe_to_run_twice(self):
        # The runner re-runs anything whose last successful run is older than
        # the interval, and an operator recovering from an outage runs it by
        # hand, so twice in a row is the ordinary case.
        services.define_item("ESP-01", name="Espresso beans")
        services.receive("ESP-01", 5)
        services.reserve("ESP-01", 5, reference="basket-1", minutes=1)

        later = timezone.now() + timedelta(minutes=2)

        self.assertEqual(1, services.expire_reservations(now=later))
        self.assertEqual(0, services.expire_reservations(now=later))

    def test_the_sweep_worker_is_safe_to_run_twice(self):
        services.define_item("ESP-01", name="Espresso beans", reorder_point=5)
        services.receive("ESP-01", 1)

        self.assertEqual(1, services.run_low_stock_sweep()["raised"])
        self.assertEqual(0, services.run_low_stock_sweep()["raised"])


@skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
class HealthCheckTests(TestCase):
    def test_the_health_check_passes_on_a_working_install(self):
        from knight_feature_advanced_inventory.checks import health

        self.assertTrue(health())

    def test_the_health_check_fails_when_the_ledger_cannot_be_read(self):
        # A check that always passes turns a failed install into a silent one.
        from unittest.mock import patch

        from knight_feature_advanced_inventory.checks import health

        with patch(
            "knight_feature_advanced_inventory.services.levels",
            side_effect=Exception("relation does not exist"),
        ):
            self.assertFalse(health())

    def test_the_health_check_fails_when_the_extension_is_missing(self):
        # The install that has to fail: package fine, migrations applied, and the
        # first member of staff to type into the stock picker gets an error
        # (docs/adr/0031).
        from unittest.mock import patch

        from knight_feature_advanced_inventory.checks import health

        with patch(
            "knight_feature_advanced_inventory.services.find_items",
            side_effect=Exception('operator class "gin_trgm_ops" does not exist'),
        ):
            self.assertFalse(health())


class TheStoreDefinesItsOwnStockItemsTests(TestCase):
    """
    The seam, from the store's side. Runs whether or not the Feature is present,
    because the command has to behave either way — the same shape as the search
    reindex, and for the same reason: a Feature may not read `apps.catalog`, so
    the store hands the definitions over.
    """

    def test_the_sync_command_reports_rather_than_failing(self):
        from io import StringIO

        from django.core.management import call_command

        out = StringIO()
        call_command("knight_sync_inventory", stdout=out)
        output = out.getvalue()

        self.assertTrue(
            "nothing to sync" in output or "Defined" in output,
            f"The command finished without saying what it did: {output!r}",
        )

    @skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
    def test_it_defines_an_item_per_sku_and_skips_the_ones_without(self):
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.catalog.models import Category, Product, ProductVariant

        category = Category.objects.create(name="Coffee", slug="coffee")
        product = Product.objects.create(
            name="Ethiopia Yirgacheffe",
            slug="ethiopia-yirgacheffe",
            category=category,
            status="Active",
            base_price=Decimal("420000"),
        )
        ProductVariant.objects.create(product=product, name="250g", sku="ETH-250", price=Decimal("420000"))
        ProductVariant.objects.create(product=product, name="1kg", sku="", price=Decimal("1500000"))

        out = StringIO()
        call_command("knight_sync_inventory", stdout=out)

        self.assertEqual(_d(0), services.on_hand("ETH-250"))
        self.assertIn("Skipped 1 variant", out.getvalue())

    @skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
    def test_a_second_run_does_not_wipe_a_merchants_reorder_point(self):
        # The failure this prevents is silent: a nightly sync that reset every
        # reorder point to zero would switch off every low-stock alert the
        # merchant had set up, and the list would simply look empty.
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.catalog.models import Category, Product, ProductVariant

        category = Category.objects.create(name="Coffee", slug="coffee")
        product = Product.objects.create(
            name="Ethiopia Yirgacheffe",
            slug="ethiopia-yirgacheffe",
            category=category,
            status="Active",
            base_price=Decimal("420000"),
        )
        ProductVariant.objects.create(product=product, name="250g", sku="ETH-250", price=Decimal("420000"))

        call_command("knight_sync_inventory", stdout=StringIO())
        services.define_item("ETH-250", name="Ethiopia 250g", reorder_point=5, reorder_quantity=20)
        call_command("knight_sync_inventory", stdout=StringIO())

        self.assertEqual(_d(5), StockItem.objects.get(sku="ETH-250").reorder_point)

    @skipUnless(INSTALLED, "The advanced-inventory Feature is not installed.")
    def test_a_movement_survives_a_resync(self):
        # Definitions are corrected by a sync; history is not touched by one.
        from io import StringIO

        from django.core.management import call_command

        services.define_item("ETH-250", name="Ethiopia 250g")
        services.receive("ETH-250", 12)

        call_command("knight_sync_inventory", stdout=StringIO())

        self.assertEqual(_d(12), services.on_hand("ETH-250"))
