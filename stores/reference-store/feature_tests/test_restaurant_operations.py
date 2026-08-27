"""
`restaurant-operations`, installed.

What is worth pinning here is not "a ticket changes state". It is the five
things that make a restaurant Feature either trustworthy or the reason service
went wrong on a Saturday:

- a promise is the **longest dish plus the queue**, never the sum of the dishes;
- a table has **one open session**, and the database is what says so;
- a scheduled ticket appears **when its time comes, not when a job runs**;
- a slot's remaining space is **derived from its bookings** and counts holds by
  time, so a restaurant whose cron never ran still quotes honestly;
- and booking is right **under concurrency**, which is the same claim
  `advanced-inventory` demonstrated for stock and the same demonstration:
  `ConcurrentBookingTests` races two threads for the last space in a slot.
"""

import threading
from datetime import datetime, timedelta
from unittest import skipUnless

from django.db import connection, transaction
from django.db.utils import IntegrityError
from django.test import TestCase, TransactionTestCase
from django.utils import timezone

from feature_tests.support import installed, require

APP = "knight_feature_restaurant_operations"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_restaurant_operations import services
    from knight_feature_restaurant_operations.models import (
        BookingState,
        CapacitySlot,
        KitchenTicket,
        LineState,
        ServiceStyle,
        SlotBooking,
        TableSession,
        TicketEvent,
        TicketState,
    )


def _at(hour: int, minute: int = 0, *, days: int = 0):
    """A timestamp today at a fixed time, so a slot's start is predictable."""
    moment = timezone.localtime(timezone.now()) + timedelta(days=days)

    return moment.replace(hour=hour, minute=minute, second=0, microsecond=0)


def _line(sku="", *, name="Thing", quantity=1, modifications=""):
    """One requested line, in the plain-dictionary shape `open_ticket` takes."""
    return {
        "sku": sku,
        "name": name,
        "quantity": quantity,
        "modifications": modifications,
    }


@skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
class PromiseTests(TestCase):
    """The arithmetic a restaurant is judged on."""

    def setUp(self):
        services.define_station("GRILL", name="Grill", throughput_units_per_hour=60)
        services.define_prep("BURGER", name="Burger", station="GRILL", prep_minutes=12, load_units=4)
        services.define_prep("FRIES", name="Fries", station="GRILL", prep_minutes=6, load_units=2)
        services.define_prep("WINE", name="Wine", prep_minutes=0, load_units=0, is_prepared=False)

    def test_a_promise_is_the_longest_dish_not_the_sum(self):
        # Four things that arrive together in twelve minutes must not be quoted
        # as thirty-six. This is the single claim the module is built on.
        now = timezone.now()
        promised = services.promise(
            [_line("BURGER"), _line("FRIES"), _line("FRIES"), _line("WINE")], now=now
        )

        self.assertEqual(12, round((promised - now).total_seconds() / 60))

    def test_a_backlog_makes_the_promise_longer(self):
        # Sixty units an hour, twelve units already queued: twelve minutes of
        # queue in front of whatever is ordered next.
        services.open_ticket([_line("BURGER"), _line("BURGER"), _line("BURGER")])

        now = timezone.now()
        promised = services.promise([_line("FRIES")], now=now)

        self.assertEqual(6 + 12, round((promised - now).total_seconds() / 60))

    def test_a_finished_ticket_stops_weighing_on_the_kitchen(self):
        ticket = services.open_ticket([_line("BURGER"), _line("BURGER"), _line("BURGER")])
        services.advance(ticket.number, TicketState.PREPARING)
        services.advance(ticket.number, TicketState.READY)

        self.assertEqual(0, services.load().outstanding_units)
        self.assertEqual(0, services.load().backlog_minutes)

    def test_an_unprofiled_item_is_assumed_to_take_time_rather_than_none(self):
        # The worst possible default would be zero: an unmeasured dish counted as
        # instant makes the kitchen look empty exactly when it is not.
        now = timezone.now()
        promised = services.promise([_line("NOT-ON-THE-MENU")], now=now)

        self.assertEqual(10, round((promised - now).total_seconds() / 60))

    def test_something_the_kitchen_does_not_make_adds_nothing(self):
        now = timezone.now()

        self.assertEqual(0, round((services.promise([_line("WINE")], now=now) - now).total_seconds() / 60))

    def test_quantity_multiplies_the_load_and_not_the_minutes(self):
        # A second portion goes in the same pan. It costs the kitchen capacity,
        # not wall-clock time.
        ticket = services.open_ticket([_line("BURGER", quantity=3)])
        line = ticket.lines.get()

        self.assertEqual(12, line.prep_minutes)
        self.assertEqual(12, line.load_units)

    def test_the_promise_is_stored_rather_than_recomputed(self):
        # A promise that restated itself every time somebody opened the screen
        # would be an estimate, and nobody could ever be late.
        ticket = services.open_ticket([_line("BURGER")])
        promised = ticket.promised_at

        services.open_ticket([_line("BURGER"), _line("BURGER")])
        ticket.refresh_from_db()

        self.assertEqual(promised, ticket.promised_at)

    def test_a_line_keeps_the_prep_time_it_was_opened_with(self):
        # A chef who shortens a recipe this afternoon must not retroactively make
        # this morning's tickets look late.
        ticket = services.open_ticket([_line("BURGER")])
        services.define_prep("BURGER", name="Burger", station="GRILL", prep_minutes=2, load_units=1)

        self.assertEqual(12, ticket.lines.get().prep_minutes)


@skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
class TicketWorkflowTests(TestCase):
    """The kitchen's clock, which is not the shopper's."""

    def setUp(self):
        services.define_station("GRILL", name="Grill")
        services.define_prep("BURGER", name="Burger", station="GRILL", prep_minutes=10, load_units=2)
        services.define_prep("SALAD", name="Salad", prep_minutes=4, load_units=1)

    def test_a_ticket_carries_an_order_number_and_not_a_foreign_key(self):
        # The whole base-store split in one assertion: uninstalling this Feature
        # may not take an order with it.
        ticket = services.open_ticket([_line("BURGER")], order_number=4471)

        self.assertEqual(4471, ticket.source_order_number)
        self.assertNotIn(
            "order",
            [field.name for field in KitchenTicket._meta.get_fields()],
        )

    def test_a_ticket_cannot_change_state_without_leaving_a_trace(self):
        ticket = services.open_ticket([_line("BURGER")])
        services.advance(ticket.number, TicketState.PREPARING, actor="sam")
        services.advance(ticket.number, TicketState.READY, actor="sam")

        moves = [(event.from_state, event.to_state) for event in services.history(ticket.number)]

        self.assertEqual(
            [("", TicketState.QUEUED), (TicketState.QUEUED, TicketState.PREPARING), (TicketState.PREPARING, TicketState.READY)],
            moves,
        )

    def test_a_ticket_cannot_skip_the_grill(self):
        ticket = services.open_ticket([_line("BURGER")])

        with self.assertRaises(services.InvalidTransition):
            services.advance(ticket.number, TicketState.SERVED)

    def test_a_dish_sent_back_goes_back_to_preparing(self):
        # The most common thing that happens at a pass. A workflow with no way to
        # say it is one staff work around by opening a second ticket, which loses
        # the connection to the order that is actually wrong.
        ticket = services.open_ticket([_line("BURGER")])
        services.advance(ticket.number, TicketState.PREPARING)
        services.advance(ticket.number, TicketState.READY)
        moved = services.advance(ticket.number, TicketState.PREPARING, note="sent back")

        self.assertEqual(TicketState.PREPARING, moved.state)

    def test_the_moment_the_kitchen_picked_it_up_is_not_rewritten(self):
        ticket = services.open_ticket([_line("BURGER")])
        services.advance(ticket.number, TicketState.PREPARING)
        started = KitchenTicket.objects.get(pk=ticket.pk).started_at

        services.advance(ticket.number, TicketState.READY)
        services.advance(ticket.number, TicketState.PREPARING, note="sent back")

        self.assertEqual(started, KitchenTicket.objects.get(pk=ticket.pk).started_at)

    def test_bumping_the_last_line_finishes_the_ticket(self):
        # Derived where it can be: a ticket is ready because everything on it is,
        # not because somebody said so.
        ticket = services.open_ticket([_line("BURGER"), _line("SALAD")])
        first, second = list(ticket.lines.all())

        services.bump_line(first.pk)
        ticket.refresh_from_db()
        self.assertEqual(TicketState.PREPARING, ticket.state)

        services.bump_line(second.pk)
        ticket.refresh_from_db()
        self.assertEqual(TicketState.READY, ticket.state)

    def test_serving_stays_a_decision(self):
        # The two states that are genuinely decisions rather than facts are not
        # derived, and a finished ticket must still be handed over by a person.
        ticket = services.open_ticket([_line("SALAD")])
        services.bump_line(ticket.lines.get().pk)
        ticket.refresh_from_db()

        self.assertEqual(TicketState.READY, ticket.state)

    def test_bumping_a_ticket_carries_its_lines(self):
        # A kitchen display has one button per ticket. Lines left behind would be
        # a queue of phantom work nobody can clear.
        ticket = services.open_ticket([_line("BURGER"), _line("SALAD")])
        services.advance(ticket.number, TicketState.PREPARING)

        self.assertEqual(
            [LineState.PREPARING, LineState.PREPARING],
            [line.state for line in ticket.lines.all()],
        )

    def test_ticket_numbers_are_short_and_unique(self):
        first = services.open_ticket([_line("SALAD")])
        second = services.open_ticket([_line("SALAD")])

        self.assertEqual(first.number + 1, second.number)
        self.assertLess(second.number, 10_000)

    def test_a_ticket_needs_at_least_one_line(self):
        with self.assertRaises(services.RestaurantError):
            services.open_ticket([])

    def test_modifications_survive_the_journey_to_the_kitchen(self):
        # The single most important field on a restaurant ticket and the one most
        # often lost between the till and the pass.
        ticket = services.open_ticket([_line("BURGER", modifications="no onions")])

        self.assertEqual("no onions", ticket.lines.get().modifications)


@skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
class ScheduledTicketTests(TestCase):
    """A pre-order is not work the kitchen is carrying yet."""

    def setUp(self):
        services.define_prep("PIZZA", name="Pizza", prep_minutes=15, load_units=5)

    def test_a_pre_order_stays_off_the_board_until_its_time(self):
        later = timezone.now() + timedelta(hours=3)
        ticket = services.open_ticket([_line("PIZZA")], start_after=later)

        self.assertEqual(TicketState.SCHEDULED, ticket.state)
        self.assertEqual([], services.board())

    def test_it_appears_by_time_and_not_because_a_job_ran(self):
        # The ordering `advanced-inventory` insists on for expiring holds: the
        # answer is right whether or not the cron entry exists.
        later = timezone.now() + timedelta(minutes=30)
        ticket = services.open_ticket([_line("PIZZA")], start_after=later)

        board = services.board(now=later + timedelta(minutes=1))

        self.assertEqual([ticket.number], [found.number for found in board])
        self.assertEqual(TicketState.SCHEDULED, KitchenTicket.objects.get(pk=ticket.pk).state)

    def test_the_worker_only_tidies_the_stored_state(self):
        later = timezone.now() + timedelta(minutes=30)
        ticket = services.open_ticket([_line("PIZZA")], start_after=later)

        moved = services.release_scheduled(now=later + timedelta(minutes=1))

        self.assertEqual(1, moved)
        self.assertEqual(TicketState.QUEUED, KitchenTicket.objects.get(pk=ticket.pk).state)

    def test_a_pre_order_does_not_weigh_on_the_kitchen_this_afternoon(self):
        services.open_ticket([_line("PIZZA")], start_after=timezone.now() + timedelta(hours=4))

        self.assertEqual(0, services.load().outstanding_units)

    def test_a_pre_order_is_promised_from_when_it_may_be_started(self):
        later = timezone.now() + timedelta(hours=3)
        ticket = services.open_ticket([_line("PIZZA")], start_after=later)

        self.assertGreater(ticket.promised_at, later)


@skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
class FloorTests(TestCase):
    """One table, one party."""

    def setUp(self):
        services.define_area("MAIN", name="Main room")
        services.define_table("12", name="Twelve", area="MAIN", seats=4)
        services.define_prep("SALAD", name="Salad", prep_minutes=4, load_units=1)

    def test_a_table_takes_one_party_at_a_time(self):
        services.seat("12", party_size=2)

        with self.assertRaises(services.TableInUse):
            services.seat("12", party_size=3)

    def test_the_database_is_what_actually_says_so(self):
        # The service check is a sentence somebody can act on. This is the
        # guarantee: two parties' food arriving on one bill is not prevented by a
        # code path that can be bypassed.
        seated = services.seat("12")

        with self.assertRaises(IntegrityError), transaction.atomic():
            TableSession.objects.create(table_id=seated.table_id, party_size=1)

        self.assertEqual(1, TableSession.objects.filter(closed_at__isnull=True).count())

    def test_a_table_can_be_used_again_after_it_is_cleared(self):
        services.seat("12")
        services.clear("12")
        again = services.seat("12", party_size=5)

        self.assertEqual(5, again.party_size)

    def test_clearing_a_table_twice_is_not_a_mistake(self):
        services.seat("12")
        services.clear("12")

        self.assertIsNone(services.clear("12"))

    def test_a_table_out_of_service_is_not_seated(self):
        services.define_table("12", is_active=False)

        with self.assertRaises(services.RestaurantError):
            services.seat("12")

    def test_ordering_needs_somebody_sitting_there(self):
        with self.assertRaises(services.RestaurantError):
            services.open_ticket([_line("SALAD")], table="12")

    def test_the_floor_shows_what_is_happening_at_each_table(self):
        services.seat("12", party_size=3, label="window")
        services.open_ticket([_line("SALAD")], table="12")

        status = services.floor()[0]

        self.assertTrue(status.is_seated)
        self.assertEqual(3, status.party_size)
        self.assertEqual("window", status.label)
        self.assertEqual(1, status.open_tickets)

    def test_clearing_a_table_leaves_food_that_is_still_cooking_alone(self):
        # Food on the grill is on the grill whatever the till says. Cancelling it
        # here would take a dish out of the kitchen's sight while it cooked.
        services.seat("12")
        ticket = services.open_ticket([_line("SALAD")], table="12")
        services.clear("12")

        self.assertEqual(TicketState.QUEUED, KitchenTicket.objects.get(pk=ticket.pk).state)

    def test_an_abandoned_session_is_closed_and_labelled_as_such(self):
        # A covers report that counted tables nobody served would be worse than
        # no report.
        services.seat("12")
        closed = services.close_abandoned_sessions(now=timezone.now() + timedelta(hours=9))

        self.assertEqual(1, closed)
        self.assertEqual(services.ABANDONED, TableSession.objects.get().closed_reason)

    def test_a_party_still_eating_is_not_swept_out_from_under_them(self):
        services.seat("12")

        self.assertEqual(0, services.close_abandoned_sessions(now=timezone.now() + timedelta(hours=1)))


@skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
class CapacityTests(TestCase):
    """Throttling: a time offered is a time that can be taken."""

    def setUp(self):
        self.slot_at = _at(18, 30, days=1)
        CapacitySlot.objects.create(
            starts_at=self.slot_at,
            service=ServiceStyle.COLLECTION,
            capacity_units=5,
        )

    def _book(self, reference, units=1, **kwargs):
        return services.book(self.slot_at, reference=reference, units=units, **kwargs)

    def test_what_is_left_is_derived_from_the_bookings(self):
        self._book("order-1", units=2)

        self.assertEqual(3, services.remaining(CapacitySlot.objects.get()))
        self.assertNotIn(
            "remaining_units",
            [field.name for field in CapacitySlot._meta.get_fields()],
        )

    def test_a_hold_counts_immediately(self):
        # The whole point. A time shown but not taken is a time two people are
        # given, and one of them has already left the house.
        self._book("order-1", units=5)

        self.assertEqual(0, services.remaining(CapacitySlot.objects.get()))

    def test_a_hold_stops_counting_by_time_rather_than_by_state(self):
        self._book("order-1", units=5, hold_minutes=10)
        later = timezone.now() + timedelta(minutes=11)

        self.assertEqual(5, services.remaining(CapacitySlot.objects.get(), now=later))
        self.assertEqual(BookingState.HELD, SlotBooking.objects.get().state)

    def test_the_worker_changes_no_answer(self):
        self._book("order-1", units=5, hold_minutes=10)
        later = timezone.now() + timedelta(minutes=11)

        before = services.remaining(CapacitySlot.objects.get(), now=later)
        services.expire_bookings(now=later)

        self.assertEqual(before, services.remaining(CapacitySlot.objects.get(), now=later))
        self.assertEqual(BookingState.EXPIRED, SlotBooking.objects.get().state)

    def test_a_full_slot_is_refused_with_the_numbers(self):
        self._book("order-1", units=4)

        with self.assertRaises(services.NoCapacity) as refusal:
            self._book("order-2", units=3)

        self.assertEqual(1, refusal.exception.remaining)

    def test_a_retried_checkout_gets_its_own_booking_back(self):
        first = self._book("order-1", units=2)
        again = self._book("order-1", units=2)

        self.assertEqual(first.pk, again.pk)
        self.assertEqual(1, SlotBooking.objects.count())

    def test_reusing_a_reference_for_a_settled_booking_is_named(self):
        self._book("order-1", units=2)
        services.cancel_booking("order-1")

        with self.assertRaises(services.RestaurantError):
            self._book("order-1", units=2)

    def test_a_confirmed_booking_survives_the_expiry_sweep(self):
        self._book("order-1", units=2, hold_minutes=1)
        services.confirm("order-1")

        services.expire_bookings(now=timezone.now() + timedelta(hours=2))

        self.assertEqual(BookingState.CONFIRMED, SlotBooking.objects.get().state)
        self.assertEqual(3, services.remaining(CapacitySlot.objects.get(), now=timezone.now() + timedelta(hours=2)))

    def test_confirming_twice_needs_only_one_slot(self):
        self._book("order-1", units=2)
        services.confirm("order-1")
        services.confirm("order-1")

        self.assertEqual(1, SlotBooking.objects.count())

    def test_only_times_that_can_be_taken_are_offered(self):
        self._book("order-1", units=5)

        self.assertEqual([], [offer.starts_at for offer in services.offers(within_hours=48)])

    def test_a_closed_slot_is_neither_offered_nor_bookable(self):
        CapacitySlot.objects.update(is_open=False)

        self.assertEqual([], services.offers(within_hours=48))

        with self.assertRaises(services.NoCapacity):
            self._book("order-1")

    def test_a_time_that_has_passed_is_not_offered(self):
        self.assertEqual([], services.offers(now=self.slot_at + timedelta(minutes=1), within_hours=48))

    def test_slots_are_laid_out_without_disturbing_the_ones_that_exist(self):
        # A slot a manager closed for a coach party must survive the next time
        # anybody generates the week.
        CapacitySlot.objects.update(is_open=False, capacity_units=1)
        day = timezone.localtime(self.slot_at).date()

        created = services.ensure_slots(
            day,
            opens=datetime.min.time().replace(hour=18),
            closes=datetime.min.time().replace(hour=19),
            minutes=15,
        )

        kept = CapacitySlot.objects.get(starts_at=self.slot_at)

        self.assertEqual(3, len(created))
        self.assertFalse(kept.is_open)
        self.assertEqual(1, kept.capacity_units)

    def test_an_unknown_slot_is_named_rather_than_created(self):
        with self.assertRaises(services.UnknownSlot):
            services.book(self.slot_at + timedelta(days=30), reference="order-1")


@skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
class ConcurrentBookingTests(TransactionTestCase):
    """
    Two checkouts, one space, two connections.

    The same demonstration `advanced-inventory` makes for stock, for the same
    reason: the claim that `select_for_update` prevents the oversell is worth
    nothing until two real threads on two real connections have raced for the
    last space. Removing the lock from `book()` and re-running this makes both
    succeed, which is the double-booking the whole capacity model exists to
    prevent.
    """

    available_apps = None

    def setUp(self):
        self.slot_at = _at(19, 0, days=1)
        CapacitySlot.objects.create(
            starts_at=self.slot_at,
            service=ServiceStyle.COLLECTION,
            capacity_units=1,
        )

    def test_only_one_of_two_racing_checkouts_gets_the_last_space(self):
        start = threading.Barrier(2, timeout=10)
        outcomes: list[object] = []
        lock = threading.Lock()

        def attempt(reference: str) -> None:
            try:
                start.wait()
                booking = services.book(self.slot_at, reference=reference, units=1)

                with lock:
                    outcomes.append(booking.reference)
            except services.NoCapacity as refusal:
                with lock:
                    outcomes.append(refusal)
            finally:
                # Each thread has its own connection, and a test that left them
                # open would hang the teardown rather than fail.
                connection.close()

        threads = [
            threading.Thread(target=attempt, args=("order-1",)),
            threading.Thread(target=attempt, args=("order-2",)),
        ]

        for thread in threads:
            thread.start()

        for thread in threads:
            thread.join(timeout=20)

        booked = [outcome for outcome in outcomes if isinstance(outcome, str)]
        refused = [outcome for outcome in outcomes if isinstance(outcome, services.NoCapacity)]

        self.assertEqual(1, len(booked), f"Expected exactly one booking, got {outcomes}.")
        self.assertEqual(1, len(refused), f"Expected exactly one refusal, got {outcomes}.")
        self.assertEqual(1, SlotBooking.objects.count())


@skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
class HealthTests(TestCase):
    """The check KNIGHT runs after installing this, on a restaurant with nothing in it."""

    def test_an_empty_store_is_healthy(self):
        from knight_feature_restaurant_operations import checks

        self.assertTrue(checks.health())

    def test_the_workers_run_on_an_empty_store(self):
        # A worker that raises on a quiet night is a cron entry that alerts every
        # night until somebody switches it off.
        self.assertEqual({"expired": 0, "released": 0}, services.run_slot_expiry())
        self.assertEqual({"sessions_closed": 0}, services.run_service_sweep())

    def test_nothing_here_writes_to_a_store_table(self):
        # The rule the whole delivery model rests on, asserted rather than
        # trusted: every table this Feature owns is one it declared.
        tables = {
            model._meta.db_table
            for model in KitchenTicket._meta.apps.get_app_config("knight_restaurant").get_models()
        }

        self.assertTrue(all(name.startswith("knight_restaurant_") for name in tables), tables)
        self.assertEqual(0, TicketEvent.objects.exclude(ticket__isnull=False).count())


class TheStoreHandsTheMenuAndItsOrdersOverTests(TestCase):
    """
    The seam, from the store's side. Runs whether or not the Feature is present,
    because both commands have to behave either way — the same shape as the
    inventory sync and the search reindex, and for the same reason: a Feature may
    not read `apps.catalog` or `apps.orders`, so the store hands over what it
    owns.
    """

    def test_the_commands_report_rather_than_failing(self):
        from io import StringIO

        from django.core.management import call_command

        for command in ("knight_sync_prep_times", "knight_print_kitchen_tickets"):
            out = StringIO()
            call_command(command, stdout=out)
            output = out.getvalue()

            self.assertTrue(
                "not installed" in output or "Defined" in output or "Printed" in output,
                f"{command} finished without saying what it did: {output!r}",
            )

    @skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
    def test_it_profiles_a_sku_and_skips_the_ones_without(self):
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.catalog.models import Category, Product, ProductVariant

        category = Category.objects.create(name="Mains", slug="mains")
        product = Product.objects.create(
            name="Cheeseburger",
            slug="cheeseburger",
            category=category,
            status="Active",
            base_price=Decimal("420000"),
        )
        ProductVariant.objects.create(product=product, name="Single", sku="BURG-1", price=Decimal("420000"))
        ProductVariant.objects.create(product=product, name="Double", sku="", price=Decimal("520000"))

        out = StringIO()
        call_command("knight_sync_prep_times", "--minutes", "14", stdout=out)

        profile = services.define_prep("BURG-1")

        self.assertEqual(14, profile.prep_minutes)
        self.assertIn("Skipped 1 variant", out.getvalue())

    @skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
    def test_a_second_run_does_not_print_the_same_order_twice(self):
        # A restaurant runs this every minute from cron. A second burger on the
        # grill is the failure, and it is the reason the command exists as a
        # command rather than as a call in the checkout.
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.orders.models import Order, OrderItem, OrderStatus

        order = Order.place(subtotal=Decimal("10"), total=Decimal("10"))
        order.transition_to(OrderStatus.CONFIRMED)
        item = OrderItem(
            order=order,
            source_product_id=1,
            product_name="Cheeseburger",
            unit_base_price=Decimal("10"),
            quantity=1,
        )
        item.price()
        item.save()

        call_command("knight_print_kitchen_tickets", stdout=StringIO())
        out = StringIO()
        call_command("knight_print_kitchen_tickets", stdout=out)

        self.assertEqual(1, KitchenTicket.objects.filter(source_order_number=order.number).count())
        self.assertIn("already had one", out.getvalue())

    @skipUnless(INSTALLED, "The restaurant-operations Feature is not installed.")
    def test_a_ticket_is_timed_from_the_variant_the_order_snapshotted(self):
        # A store's order line carries a variant id and no SKU. Requiring one
        # would mean every ticket opened from a real order was timed as though
        # nobody had measured the dish.
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.catalog.models import Category, Product, ProductVariant
        from apps.orders.models import Order, OrderItem, OrderStatus

        category = Category.objects.create(name="Mains", slug="mains")
        product = Product.objects.create(
            name="Pizza", slug="pizza", category=category, status="Active", base_price=Decimal("1")
        )
        variant = ProductVariant.objects.create(
            product=product, name="12 inch", sku="PIZ-12", price=Decimal("1")
        )
        services.define_prep("PIZ-12", name="Pizza", prep_minutes=18, load_units=5, object_id=variant.pk)

        order = Order.place(subtotal=Decimal("1"), total=Decimal("1"))
        order.transition_to(OrderStatus.CONFIRMED)
        item = OrderItem(
            order=order,
            source_product_id=product.pk,
            source_variant_id=variant.pk,
            product_name="Pizza",
            unit_base_price=Decimal("1"),
            quantity=2,
        )
        item.price()
        item.save()

        call_command("knight_print_kitchen_tickets", stdout=StringIO())
        line = KitchenTicket.objects.get(source_order_number=order.number).lines.get()

        self.assertEqual(18, line.prep_minutes)
        self.assertEqual(10, line.load_units)
