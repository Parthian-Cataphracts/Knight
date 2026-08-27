"""
`multi-location`, installed.

The Feature named as the dangerous one for seven phases, so the first thing this
suite pins is the claim that it is not:

- it **owns only its own tables**, and installing it migrates nobody's rows;
- a code nobody has described is **still a usable code**, which is what makes it
  adoptable a branch at a time and what the day of the install looks like;
- a route is **decided once and written down**, so editing a rule cannot move an
  order that has already been handled;
- a **shut branch is never routed to**, at any step of the fallback;
- **exactly one default**, and the database is what says so;
- and an **absent menu row means available**, because the other reading would
  hide every new product from every branch.
"""

from datetime import date, time, timedelta
from unittest import skipUnless

from django.db import transaction
from django.db.utils import IntegrityError
from django.test import TestCase
from django.utils import timezone

from feature_tests.support import installed, require

APP = "knight_feature_multi_location"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_multi_location import services
    from knight_feature_multi_location.models import (
        Location,
        LocationKind,
        OrderRouting,
        RuleKind,
    )


@skipUnless(INSTALLED, "The multi-location Feature is not installed.")
class NamingCodesTests(TestCase):
    """The whole integration: a code that already existed gets a name."""

    def test_a_code_nobody_has_described_is_still_a_code(self):
        # The state every location code in the store is in on the day this
        # Feature is installed. A raise here would break every caller at once.
        self.assertIsNone(services.describe("CAMDEN"))

    def test_describing_one_attaches_a_name_to_the_string_that_was_there(self):
        place = services.define_location("CAMDEN", name="Camden Road", city="London")

        self.assertEqual("CAMDEN", place.code)
        self.assertEqual("Camden Road", services.describe("camden").name)

    def test_codes_are_matched_however_they_were_typed(self):
        # The code is a join key other Features have been stamping on their rows
        # for two releases. A second branch created by a stray space would be a
        # branch whose stock is somewhere else.
        services.define_location(" camden ", name="Camden Road")

        self.assertEqual(1, Location.objects.count())
        self.assertIsNotNone(services.describe("CAMDEN"))

    def test_describing_it_again_corrects_rather_than_duplicates(self):
        services.define_location("CAMDEN", name="Camden Road")
        services.define_location("CAMDEN", name="Camden High Street")

        self.assertEqual(1, Location.objects.count())
        self.assertEqual("Camden High Street", services.describe("CAMDEN").name)

    def test_a_branch_that_closed_keeps_its_row(self):
        # Every movement and every ticket ever stamped with this code still
        # refers to it. Deleting the row would make all of that history anonymous
        # again.
        services.define_location("CAMDEN", name="Camden Road")
        services.define_location("CAMDEN", is_active=False)

        self.assertFalse(services.describe("CAMDEN").is_active)
        self.assertEqual([], [place.code for place in services.places()])

    def test_this_feature_owns_only_its_own_tables(self):
        # The claim the whole "deliberately late" argument turned on, asserted
        # rather than trusted.
        tables = {
            model._meta.db_table
            for model in Location._meta.apps.get_app_config("knight_locations").get_models()
        }

        self.assertTrue(all(name.startswith("knight_locations_") for name in tables), tables)

    def test_a_warehouse_is_not_somewhere_a_customer_turns_up(self):
        services.define_location("DEPOT", name="Depot", kind=LocationKind.WAREHOUSE)

        self.assertFalse(services.describe("DEPOT").takes_customers)


@skipUnless(INSTALLED, "The multi-location Feature is not installed.")
class DefaultLocationTests(TestCase):
    """Exactly one, and the database is what says so."""

    def setUp(self):
        services.define_location("CAMDEN", name="Camden Road")
        services.define_location("SOHO", name="Soho")

    def test_naming_a_new_default_clears_the_old_one(self):
        services.set_default("CAMDEN")
        services.set_default("SOHO")

        self.assertEqual("SOHO", services.default_place().code)
        self.assertEqual(1, Location.objects.filter(is_default=True).count())

    def test_the_database_refuses_a_second_default(self):
        # Two defaults is a routing table with a coin toss in it.
        services.set_default("CAMDEN")

        with self.assertRaises(IntegrityError), transaction.atomic():
            Location.objects.filter(code="SOHO").update(is_default=True)

    def test_there_may_be_none_at_all(self):
        self.assertIsNone(services.default_place())


@skipUnless(INSTALLED, "The multi-location Feature is not installed.")
class OpeningHoursTests(TestCase):
    """When a branch is trading."""

    def setUp(self):
        services.define_location("CAMDEN", name="Camden Road")
        self.monday = _next_weekday(0)

    def test_a_branch_with_no_hours_at_all_is_open(self):
        # Nobody has entered hours on the day this Feature is installed, and a
        # merchant whose every branch silently stopped taking orders would
        # rightly call that a broken release.
        self.assertTrue(services.is_open("CAMDEN"))

    def test_a_branch_with_hours_is_shut_outside_them(self):
        services.set_hours("CAMDEN", 0, time(9, 0), time(17, 0))

        self.assertTrue(services.is_open("CAMDEN", at=_at(self.monday, 12, 0)))
        self.assertFalse(services.is_open("CAMDEN", at=_at(self.monday, 18, 0)))

    def test_a_day_with_no_window_is_shut(self):
        # How a shop says "we do not open on Mondays": by giving hours to the
        # other days and none to this one.
        services.set_hours("CAMDEN", 1, time(9, 0), time(17, 0))

        self.assertFalse(services.is_open("CAMDEN", at=_at(self.monday, 12, 0)))

    def test_a_shop_that_closes_for_lunch_has_two_windows(self):
        # The reason hours are rows rather than a "09:00-17:00" string.
        services.set_hours("CAMDEN", 0, time(9, 0), time(12, 0))
        services.set_hours("CAMDEN", 0, time(14, 0), time(18, 0))

        self.assertTrue(services.is_open("CAMDEN", at=_at(self.monday, 10, 0)))
        self.assertFalse(services.is_open("CAMDEN", at=_at(self.monday, 13, 0)))
        self.assertTrue(services.is_open("CAMDEN", at=_at(self.monday, 15, 0)))

    def test_a_closure_beats_the_hours(self):
        services.set_hours("CAMDEN", 0, time(9, 0), time(17, 0))
        services.close_on("CAMDEN", self.monday, reason="bank holiday")

        self.assertFalse(services.is_open("CAMDEN", at=_at(self.monday, 12, 0)))

    def test_an_inactive_branch_is_never_open(self):
        services.define_location("CAMDEN", is_active=False)

        self.assertFalse(services.is_open("CAMDEN"))

    def test_a_branch_is_read_in_its_own_timezone(self):
        # A merchant with a branch an hour ahead has opening hours that mean
        # different moments in each. Evaluating both against the store's clock
        # would put one of them an hour wrong all year.
        services.define_location("BERLIN", name="Berlin", timezone="Europe/Berlin")
        services.define_location("LONDON", name="London", timezone="Europe/London")
        services.set_hours("BERLIN", 0, time(9, 0), time(10, 0))
        services.set_hours("LONDON", 0, time(9, 0), time(10, 0))

        # 08:30 UTC is 09:30 in London and 10:30 in Berlin.
        moment = _at(self.monday, 8, 30, utc=True)

        self.assertTrue(services.is_open("LONDON", at=moment))
        self.assertFalse(services.is_open("BERLIN", at=moment))

    def test_a_timezone_nobody_can_resolve_falls_back_rather_than_raising(self):
        # A merchant who typed the name wrong gets the store's clock, which is
        # what they had before this Feature existed — not an exception in the
        # middle of a checkout.
        services.define_location("TYPO", name="Typo", timezone="Europe/Lundon")

        self.assertTrue(services.is_open("TYPO"))


@skipUnless(INSTALLED, "The multi-location Feature is not installed.")
class MenuTests(TestCase):
    """An exception table, read the only way that stays correct."""

    def setUp(self):
        services.define_location("CAMDEN", name="Camden Road")

    def test_absence_means_available(self):
        # The other reading would mean a store installing this Feature
        # discovered that none of its branches sold anything.
        self.assertTrue(services.sells("CAMDEN", "ESP-01"))

    def test_a_branch_can_say_it_does_not_sell_something(self):
        services.set_availability("CAMDEN", "ESP-01", available=False, note="no grinder")

        self.assertFalse(services.sells("CAMDEN", "ESP-01"))
        self.assertEqual(["ESP-01"], services.unavailable_at("CAMDEN"))

    def test_saying_so_twice_corrects_rather_than_duplicates(self):
        services.set_availability("CAMDEN", "ESP-01", available=False)
        services.set_availability("CAMDEN", "ESP-01", available=True)

        self.assertTrue(services.sells("CAMDEN", "ESP-01"))
        self.assertEqual([], services.unavailable_at("CAMDEN"))

    def test_a_new_product_is_not_hidden_from_anybody(self):
        services.set_availability("CAMDEN", "ESP-01", available=False)

        self.assertTrue(services.sells("CAMDEN", "A-PRODUCT-ADDED-THIS-MORNING"))


@skipUnless(INSTALLED, "The multi-location Feature is not installed.")
class RosterTests(TestCase):
    """Who worked where, and when."""

    def setUp(self):
        services.define_location("CAMDEN", name="Camden Road")
        services.define_location("SOHO", name="Soho")
        services.define_staff("SAM", name="Sam")

    def test_somebody_can_cover_two_branches_at_once(self):
        services.assign("SAM", "CAMDEN", role="chef")
        services.assign("SAM", "SOHO", role="chef")

        self.assertEqual(["SAM"], [member.code for member in services.roster("CAMDEN")])
        self.assertEqual(["SAM"], [member.code for member in services.roster("SOHO")])

    def test_assigning_twice_returns_the_assignment_that_is_open(self):
        # The caller is usually a nightly sync of a rota that has not changed.
        first = services.assign("SAM", "CAMDEN")
        again = services.assign("SAM", "CAMDEN")

        self.assertEqual(first.pk, again.pk)

    def test_leaving_a_branch_dates_the_assignment_rather_than_deleting_it(self):
        # "Who worked at Camden last March" is a question asked after an
        # incident, and a deleted row answers it with nobody.
        yesterday = timezone.localdate() - timedelta(days=1)
        services.assign("SAM", "CAMDEN", starts_on=yesterday - timedelta(days=30))
        services.unassign("SAM", "CAMDEN", ends_on=yesterday)

        self.assertEqual([], [member.code for member in services.roster("CAMDEN")])
        self.assertEqual(
            ["SAM"],
            [member.code for member in services.roster("CAMDEN", on=yesterday - timedelta(days=1))],
        )

    def test_an_unknown_member_of_staff_is_named_rather_than_created(self):
        with self.assertRaises(services.UnknownStaffMember):
            services.assign("NOBODY", "CAMDEN")


@skipUnless(INSTALLED, "The multi-location Feature is not installed.")
class RoutingTests(TestCase):
    """Decided once, written down, and never to a branch that is shut."""

    def setUp(self):
        services.define_location("CAMDEN", name="Camden Road", city="London")
        services.define_location("SOHO", name="Soho", city="London")
        services.define_location("DEPOT", name="Depot", kind=LocationKind.WAREHOUSE)

    def test_a_rule_sends_an_order_to_a_branch(self):
        services.define_rule(RuleKind.POSTAL_PREFIX, pattern="NW1", location="CAMDEN", priority=10)

        decision = services.route(1001, postal_code="NW1 8QP")

        self.assertEqual("CAMDEN", decision.location)
        self.assertIn("NW1", decision.reason)

    def test_priority_decides_and_not_the_order_they_were_typed_in(self):
        services.define_rule(RuleKind.CITY, pattern="London", location="SOHO", priority=50)
        services.define_rule(RuleKind.POSTAL_PREFIX, pattern="NW1", location="CAMDEN", priority=10)

        self.assertEqual("CAMDEN", services.route(1002, postal_code="NW1 8QP", city="London").location)

    def test_a_postcode_is_matched_however_it_was_spaced(self):
        services.define_rule(RuleKind.POSTAL_PREFIX, pattern="nw1", location="CAMDEN")

        self.assertEqual("CAMDEN", services.route(1003, postal_code=" nw1 8qp ").location)

    def test_the_order_is_decided_once(self):
        # Where an order was handled is a fact about that order, not a function
        # of the rules that exist today.
        services.define_rule(RuleKind.ALWAYS, location="CAMDEN")
        first = services.route(1004)

        services.define_rule(RuleKind.ALWAYS, location="SOHO")
        again = services.route(1004)

        self.assertEqual(first.location, again.location)
        self.assertEqual("CAMDEN", again.location)
        self.assertEqual(1, OrderRouting.objects.count())

    def test_a_shut_branch_is_skipped_rather_than_failing_the_order(self):
        # A rule pointing at a branch that is shut on a Sunday is a correct rule
        # on the other six days.
        monday = _next_weekday(0)
        services.set_hours("CAMDEN", 0, time(9, 0), time(10, 0))
        services.define_rule(RuleKind.ALWAYS, location="CAMDEN", priority=10)
        services.set_default("SOHO")

        decision = services.route(1005, at=_at(monday, 18, 0))

        self.assertEqual("SOHO", decision.location)

    def test_what_the_shopper_asked_for_wins(self):
        services.define_rule(RuleKind.ALWAYS, location="CAMDEN")

        self.assertEqual("SOHO", services.route(1006, prefer="SOHO").location)

    def test_a_branch_the_shopper_asked_for_that_is_shut_does_not_win(self):
        monday = _next_weekday(0)
        services.set_hours("SOHO", 0, time(9, 0), time(10, 0))
        services.define_rule(RuleKind.ALWAYS, location="CAMDEN")

        self.assertEqual("CAMDEN", services.route(1007, prefer="SOHO", at=_at(monday, 18, 0)).location)

    def test_the_default_catches_what_no_rule_matched(self):
        services.set_default("SOHO")

        self.assertEqual("SOHO", services.route(1008, postal_code="ZZ99").location)

    def test_one_open_branch_needs_no_decision_from_the_merchant(self):
        # The single-site case this Feature is supposed to leave alone.
        monday = _next_weekday(0)
        services.define_location("SOHO", is_active=False)
        services.define_location("DEPOT", is_active=False)

        decision = services.route(1009, at=_at(monday, 12, 0))

        self.assertEqual("CAMDEN", decision.location)
        self.assertEqual("the only open location", decision.reason)

    def test_nowhere_open_is_refused_in_a_way_a_checkout_can_act_on(self):
        monday = _next_weekday(0)

        for code in ("CAMDEN", "SOHO", "DEPOT"):
            services.set_hours(code, 0, time(9, 0), time(10, 0))

        with self.assertRaises(services.NowhereToRouteTo):
            services.route(1010, at=_at(monday, 18, 0))

    def test_an_order_that_was_never_routed_says_so(self):
        self.assertIsNone(services.routing_for(9999))

    def test_the_explanation_survives_the_rule_being_deleted(self):
        # The same argument the store's own OrderPromotion makes about an
        # uninstalled promotions Feature: the explanation of a decision has to
        # outlive the thing that made it.
        rule = services.define_rule(RuleKind.ALWAYS, location="CAMDEN")
        services.route(1011)
        rule.delete()

        decision = services.routing_for(1011)

        self.assertEqual("CAMDEN", decision.location)
        self.assertNotEqual("", decision.reason)

    def test_an_unknown_rule_kind_is_refused(self):
        with self.assertRaises(services.LocationError):
            services.define_rule("whatever-i-fancy", location="CAMDEN")


@skipUnless(INSTALLED, "The multi-location Feature is not installed.")
class HealthTests(TestCase):
    """The check KNIGHT runs after installing this, on a merchant who has described nothing."""

    def test_a_store_that_has_described_nothing_is_healthy(self):
        from knight_feature_multi_location import checks

        self.assertTrue(checks.health())

    def test_this_feature_declares_no_scheduled_work(self):
        # Stated as a test because the manifest states it: nothing here happens
        # on a clock, and a worker added later should be a deliberate decision
        # rather than a habit.
        self.assertFalse(hasattr(services, "run_daily"))
        self.assertFalse(hasattr(services, "run_hourly"))


def _next_weekday(weekday: int) -> date:
    """The next date that falls on this weekday, so a test never depends on today."""
    today = timezone.localdate()

    return today + timedelta(days=(weekday - today.weekday()) % 7 or 7)


def _at(day: date, hour: int, minute: int = 0, *, utc: bool = False):
    """A moment on a date, in the store's timezone unless UTC is asked for."""
    from datetime import datetime, timezone as dt_timezone

    naive = datetime.combine(day, time(hour, minute))

    if utc:
        return naive.replace(tzinfo=dt_timezone.utc)

    return timezone.make_aware(naive)


INVENTORY = "knight_feature_advanced_inventory"
RESTAURANT = "knight_feature_restaurant_operations"


@skipUnless(
    INSTALLED and installed(INVENTORY) and installed(RESTAURANT),
    "Needs multi-location, advanced-inventory and restaurant-operations together.",
)
class TheCodesTheOtherFeaturesAlreadyStampedTests(TestCase):
    """
    The claim the whole phase turned on, with all three Features installed.

    `multi-location` was held back for seven phases because it "reshapes data
    other Features already own". These tests are the demonstration that it does
    not: the other two stamped a location code on their own rows from their own
    1.0, and this Feature attaches a name to that string without touching a row
    of theirs.
    """

    def setUp(self):
        from knight_feature_advanced_inventory import services as inventory
        from knight_feature_restaurant_operations import services as restaurant

        self.inventory = inventory
        self.restaurant = restaurant

        inventory.define_item("ESP-01", name="Espresso beans", unit="kg")
        inventory.receive("ESP-01", 10, location="CAMDEN")
        restaurant.define_table("12", name="Twelve", location="CAMDEN")

    def test_the_other_features_stamped_a_code_before_this_one_existed(self):
        from knight_feature_advanced_inventory.models import StockMovement

        # Nobody has described it yet, and everything still works.
        self.assertEqual("CAMDEN", StockMovement.objects.get().location)
        self.assertIsNone(services.describe("CAMDEN"))

    def test_describing_it_changes_not_one_of_their_rows(self):
        from knight_feature_advanced_inventory.models import StockMovement

        before = {
            (movement.pk, movement.location, movement.quantity)
            for movement in StockMovement.objects.all()
        }

        services.define_location("CAMDEN", name="Camden Road", city="London")

        after = {
            (movement.pk, movement.location, movement.quantity)
            for movement in StockMovement.objects.all()
        }

        self.assertEqual(before, after)
        self.assertEqual("Camden Road", services.describe("CAMDEN").name)

    def test_the_stock_at_a_named_branch_is_the_stock_at_that_code(self):
        # The join, in one assertion: a string both sides already agreed on, and
        # no foreign key in either direction.
        services.define_location("CAMDEN", name="Camden Road")

        self.assertEqual(
            10,
            int(self.inventory.on_hand("ESP-01", location=services.describe("CAMDEN").code)),
        )

    def test_neither_feature_holds_a_reference_to_the_other(self):
        from knight_feature_advanced_inventory.models import StockMovement
        from knight_feature_restaurant_operations.models import Table

        for model in (StockMovement, Table, Location):
            related = [
                field.name
                for field in model._meta.get_fields()
                if field.is_relation and field.related_model is not None
                and field.related_model._meta.app_label != model._meta.app_label
            ]

            self.assertEqual([], related, f"{model.__name__} reaches into another app.")

    def test_a_branch_nobody_described_still_holds_stock_and_tables(self):
        # Gradual adoption: a merchant names Camden this week and Soho next
        # month, and nothing in between is broken.
        self.inventory.receive("ESP-01", 4, location="SOHO")
        services.define_location("CAMDEN", name="Camden Road")

        self.assertIsNone(services.describe("SOHO"))
        self.assertEqual(4, int(self.inventory.on_hand("ESP-01", location="SOHO")))


class TheStoreHandsItsOrdersOverTests(TestCase):
    """
    The seam, from the store's side. Runs whether or not the Feature is present,
    because the command has to behave either way — the same shape as every other
    sync in this store, and for the same reason: a Feature may not read
    `apps.orders`.
    """

    def test_the_command_reports_rather_than_failing(self):
        from io import StringIO

        from django.core.management import call_command

        out = StringIO()
        call_command("knight_route_orders", stdout=out)
        output = out.getvalue()

        self.assertTrue(
            "not installed" in output or "Routed" in output,
            f"The command finished without saying what it did: {output!r}",
        )

    @skipUnless(INSTALLED, "The multi-location Feature is not installed.")
    def test_it_routes_by_the_address_the_order_snapshotted(self):
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.orders.models import FulfillmentMethod, Order, OrderFulfillment, OrderStatus

        services.define_location("CAMDEN", name="Camden Road")
        services.define_rule(RuleKind.POSTAL_PREFIX, pattern="NW1", location="CAMDEN")

        order = Order.place(subtotal=Decimal("10"), total=Decimal("10"))
        order.transition_to(OrderStatus.CONFIRMED)
        OrderFulfillment.objects.create(
            order=order,
            method=FulfillmentMethod.DELIVERY,
            address_line1="1 Camden Road",
            city="London",
            postal_code="NW1 8QP",
        )

        call_command("knight_route_orders", stdout=StringIO())

        self.assertEqual("CAMDEN", services.routing_for(order.number).location)

    @skipUnless(INSTALLED, "The multi-location Feature is not installed.")
    def test_an_order_nowhere_can_take_is_left_for_the_next_run(self):
        # Two in the morning with every branch shut is a real state, and an order
        # forced into a closed kitchen is one nobody cooks.
        from decimal import Decimal
        from io import StringIO

        from django.core.management import call_command

        from apps.orders.models import Order, OrderStatus

        services.define_location("CAMDEN", name="Camden Road", is_active=False)

        order = Order.place(subtotal=Decimal("10"), total=Decimal("10"))
        order.transition_to(OrderStatus.CONFIRMED)

        out = StringIO()
        call_command("knight_route_orders", stdout=out)

        self.assertIsNone(services.routing_for(order.number))
        self.assertIn("nowhere open", out.getvalue())
