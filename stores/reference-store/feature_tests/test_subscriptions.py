"""
`subscriptions`, installed.

The first Feature in the catalogue whose worst failure is not losing data. It is
charging somebody who did not owe it, so what is pinned here is not "billing
works" but the six things that decide whether a merchant can trust it with a
customer's card:

- a period is billed **once**, and the **database** is what says so;
- two workers racing the same subscription produce **one charge**, demonstrated
  with two threads on two connections rather than argued;
- pausing **moves the clock forward** rather than accruing, so a shopper who
  pauses for three months and comes back is charged once;
- a failed payment **stops the clock**, so nobody ends up owing two periods;
- **nothing charges without a provider**, and the default provider refuses by
  name rather than pretending;
- and the **ledger is append-only**, so "why was I charged twice in March" is a
  query.
"""

import threading
from datetime import date, timedelta
from decimal import Decimal
from unittest import skipUnless

from django.db import connection, transaction
from django.db.utils import IntegrityError
from django.test import TestCase, TransactionTestCase
from django.utils import timezone

from feature_tests.support import installed, require

APP = "knight_feature_subscriptions"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_subscriptions import providers, services
    from knight_feature_subscriptions.models import (
        AttemptOutcome,
        BillingAttempt,
        BillingPeriod,
        Interval,
        PeriodState,
        Subscription,
        SubscriptionState,
    )


def _d(value) -> Decimal:
    return Decimal(str(value)).quantize(Decimal("0.01"))


def _line(sku="BEANS", name="Coffee beans", quantity=1, unit_price="10.00"):
    return {"sku": sku, "name": name, "quantity": quantity, "unit_price": unit_price}


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class BillingOnceTests(TestCase):
    """The claim the whole package is arranged around."""

    def setUp(self):
        services.create("sub-1", amount="10.00", lines=[_line()], provider=providers.MANUAL)
        services.activate("sub-1")

    def test_a_due_subscription_is_charged_once(self):
        charge = services.bill("sub-1")

        self.assertTrue(charge.succeeded)
        self.assertEqual(1, BillingPeriod.objects.filter(state=PeriodState.PAID).count())

    def test_billing_the_same_period_twice_is_refused_rather_than_charged(self):
        services.bill("sub-1")

        # Not an error the caller should alert on: this is what a webhook
        # delivered twice looks like, and webhooks do that.
        charge = services.bill("sub-1")

        self.assertFalse(charge.succeeded)
        self.assertEqual(1, BillingAttempt.objects.filter(outcome=AttemptOutcome.SUCCEEDED).count())

    def test_the_database_refuses_a_second_period_of_the_same_number(self):
        # The guarantee. Every other check in this package is code that a caller
        # could route around; this one cannot be routed around by anybody.
        services.bill("sub-1")
        subscription = Subscription.objects.get(reference="sub-1")

        with self.assertRaises(IntegrityError), transaction.atomic():
            BillingPeriod.objects.create(
                subscription=subscription,
                sequence=1,
                starts_on=date(2026, 1, 1),
                ends_on=date(2026, 1, 31),
                amount=Decimal("10.00"),
            )

    def test_nothing_is_charged_before_it_is_due(self):
        services.bill("sub-1")
        charge = services.bill("sub-1", now=timezone.now() + timedelta(days=1))

        self.assertEqual(AttemptOutcome.REFUSED, charge.outcome)
        self.assertIn("nothing is due yet", charge.detail)

    def test_the_next_period_is_charged_when_its_time_comes(self):
        services.bill("sub-1")
        later = timezone.now() + timedelta(days=40)

        charge = services.bill("sub-1", now=later)

        self.assertTrue(charge.succeeded)
        self.assertEqual(2, charge.sequence)
        self.assertEqual(_d(20), services.summarise("sub-1").paid_to_date)

    def test_periods_do_not_overlap(self):
        services.bill("sub-1")
        services.bill("sub-1", now=timezone.now() + timedelta(days=40))

        first, second = services.periods("sub-1")

        self.assertEqual(first.ends_on + timedelta(days=1), second.starts_on)


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class PauseAndResumeTests(TestCase):
    """The failure that makes the news."""

    def setUp(self):
        services.create("sub-1", amount="10.00", lines=[_line()], provider=providers.MANUAL)
        services.activate("sub-1")
        services.bill("sub-1")

    def test_a_paused_subscription_is_not_due_at_all(self):
        services.pause("sub-1")

        self.assertEqual([], list(services.due(now=timezone.now() + timedelta(days=400))))

    def test_a_paused_subscription_refuses_to_be_billed_even_when_forced(self):
        services.pause("sub-1")

        charge = services.bill("sub-1", force=True)

        self.assertEqual(AttemptOutcome.REFUSED, charge.outcome)

    def test_resuming_after_three_months_charges_once_and_not_four_times(self):
        # The whole reason `resume` sets the clock to now. Charging for time
        # nobody received is what a shopper reads as being robbed for going on
        # holiday.
        services.pause("sub-1")

        later = timezone.now() + timedelta(days=95)
        services.resume("sub-1", now=later)
        counts = services.bill_due(now=later)

        self.assertEqual(1, counts["billed"])
        self.assertEqual(_d(20), services.summarise("sub-1").paid_to_date)

    def test_a_cancelled_subscription_keeps_what_it_was_paid(self):
        # A cancellation is the end of the future, not a rewriting of the past.
        services.cancel("sub-1", reason="changed their mind")
        summary = services.summarise("sub-1")

        self.assertEqual(SubscriptionState.CANCELLED, summary.state)
        self.assertEqual(_d(10), summary.paid_to_date)
        self.assertEqual(1, BillingPeriod.objects.filter(state=PeriodState.PAID).count())

    def test_a_cancelled_subscription_cannot_be_resumed(self):
        services.cancel("sub-1")

        with self.assertRaises(services.InvalidTransition):
            services.resume("sub-1")

    def test_every_move_leaves_a_trace(self):
        services.pause("sub-1", actor="sam", reason="holiday")
        services.resume("sub-1", actor="sam")

        moves = [(event.from_state, event.to_state) for event in services.history("sub-1")]

        self.assertEqual(
            [
                ("", SubscriptionState.PENDING),
                (SubscriptionState.PENDING, SubscriptionState.ACTIVE),
                (SubscriptionState.ACTIVE, SubscriptionState.PAUSED),
                (SubscriptionState.PAUSED, SubscriptionState.ACTIVE),
            ],
            moves,
        )


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class FailedPaymentTests(TestCase):
    """What happens when the card says no."""

    def setUp(self):
        # `api` with no secret refuses, which is the closest honest thing to a
        # declined card this repository has: the point being tested is what the
        # Feature does with a charge that did not succeed.
        services.create("sub-1", amount="10.00", lines=[_line()], provider=providers.API)
        services.activate("sub-1")

    def test_a_failed_charge_stops_the_clock(self):
        # Otherwise the next period opens on top of one nobody has paid, and the
        # shopper owes two.
        services.bill("sub-1")
        subscription = Subscription.objects.get(reference="sub-1")

        self.assertIsNone(subscription.next_run_at)
        self.assertEqual([], list(services.due(now=timezone.now() + timedelta(days=400))))

    def test_a_refusal_is_not_reported_as_a_declined_payment(self):
        # A failure is the provider saying no. A refusal is us not asking. A
        # merchant told the first when the truth is the second goes looking for a
        # problem with their payment provider that does not exist.
        charge = services.bill("sub-1")

        self.assertEqual(AttemptOutcome.REFUSED, charge.outcome)
        self.assertIn("payment_api_key", charge.detail)

    def test_a_configured_provider_with_no_vendor_says_so_rather_than_pretending(self):
        # The far end of the honest-refusal path: the secret is there, the
        # payment method is there, and there is still no vendor wired up. Saying
        # so is the whole point - a plausible-looking call to a provider nobody
        # has an account with would be worse.
        from unittest.mock import patch

        services.create(
            "sub-2",
            amount="10.00",
            provider=providers.API,
            payment_method_reference="pm_test",
        )
        services.activate("sub-2")

        with patch("knight_feature_subscriptions.config.secret", return_value="a-key"):
            charge = services.bill("sub-2")

        self.assertEqual(AttemptOutcome.REFUSED, charge.outcome)
        self.assertIn("not wired to a vendor", charge.detail)

    def test_a_retry_reuses_the_period_rather_than_opening_a_second(self):
        services.bill("sub-1")
        services.bill("sub-1", force=True)

        # One debt, two attempts. A retry that opened a second period would be a
        # second debt for the same month.
        self.assertEqual(1, BillingPeriod.objects.count())
        self.assertEqual(2, BillingAttempt.objects.count())

    def test_the_ledger_keeps_every_attempt(self):
        services.bill("sub-1")
        services.bill("sub-1", force=True)

        attempts = list(BillingAttempt.objects.order_by("attempt"))

        self.assertEqual([1, 2], [attempt.attempt for attempt in attempts])
        self.assertTrue(all(attempt.detail for attempt in attempts))


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class ProviderTests(TestCase):
    """Nothing charges without a provider that says it can."""

    def test_the_default_provider_refuses_by_name(self):
        result = providers.charge(
            provider="none", amount=Decimal("10.00"), currency="IRR", reference="x"
        )

        self.assertEqual(AttemptOutcome.REFUSED, result.outcome)
        self.assertIn("No payment provider is configured", result.detail)

    def test_an_unknown_provider_is_named_rather_than_absorbed(self):
        result = providers.charge(
            provider="definitely-not-a-provider", amount=Decimal("1"), currency="IRR", reference="x"
        )

        self.assertEqual(AttemptOutcome.REFUSED, result.outcome)
        self.assertIn("definitely-not-a-provider", result.detail)

    def test_the_api_provider_refuses_without_its_secret(self):
        result = providers.charge(
            provider=providers.API, amount=Decimal("1"), currency="IRR", reference="x"
        )

        self.assertEqual(AttemptOutcome.REFUSED, result.outcome)
        self.assertIn("payment_api_key", result.detail)

    def test_a_provider_that_raises_becomes_a_recorded_failure(self):
        # "We do not know whether the money moved" is the one answer a billing
        # system may not give, so nothing here is allowed to raise.
        from unittest.mock import patch

        with patch(
            "knight_feature_subscriptions.providers._manual",
            side_effect=RuntimeError("the provider fell over"),
        ):
            result = providers.charge(
                provider=providers.MANUAL, amount=Decimal("1"), currency="IRR", reference="x"
            )

        self.assertEqual(AttemptOutcome.FAILED, result.outcome)
        self.assertIn("RuntimeError", result.detail)

    def test_no_field_in_this_feature_could_hold_a_card_number(self):
        # Stated as a test because it is the design. A Feature that could hold
        # one would pull every store installing it into a compliance regime it
        # did not choose.
        names = {field.name for field in Subscription._meta.get_fields()}

        self.assertNotIn("card_number", names)
        self.assertNotIn("pan", names)
        self.assertNotIn("cvv", names)
        self.assertIn("payment_method_reference", names)


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class ScheduleTests(TestCase):
    """When the next period falls."""

    def test_a_monthly_subscription_keeps_its_day_of_the_month(self):
        # Thirty days drifts into the previous month twice a year, and a shopper
        # who agreed to be billed on the 15th notices.
        services.create(
            "sub-1", amount="10.00", interval=Interval.MONTHLY, starts_on=date(2026, 1, 15),
            provider=providers.MANUAL,
        )
        services.activate("sub-1")
        services.bill("sub-1", force=True)

        period = services.periods("sub-1")[0]

        self.assertEqual(date(2026, 1, 15), period.starts_on)
        self.assertEqual(date(2026, 2, 14), period.ends_on)

    def test_a_day_that_does_not_exist_lands_on_the_last_one(self):
        services.create(
            "sub-1", amount="10.00", interval=Interval.MONTHLY, starts_on=date(2026, 1, 31),
            provider=providers.MANUAL,
        )
        services.activate("sub-1")
        services.bill("sub-1", force=True)

        # 31 January plus a month is 28 February, so the period ends the day
        # before. Every billing system that has thought about this does the same.
        self.assertEqual(date(2026, 2, 27), services.periods("sub-1")[0].ends_on)

    def test_a_subscription_with_a_limit_ends_rather_than_billing_again(self):
        # Twelve boxes then stop is a promise a merchant made, and a thirteenth
        # charge breaks it.
        services.create("sub-1", amount="10.00", periods_limit=2, provider=providers.MANUAL)
        services.activate("sub-1")

        services.bill("sub-1")
        services.bill("sub-1", now=timezone.now() + timedelta(days=40))

        summary = services.summarise("sub-1")

        self.assertEqual(SubscriptionState.ENDED, summary.state)
        self.assertIsNone(summary.next_run_at)
        self.assertEqual(2, summary.periods_billed)

    def test_an_interval_this_feature_does_not_know_is_refused(self):
        with self.assertRaises(services.SubscriptionError):
            services.create("sub-2", amount="10.00", interval="fortnightly")

    def test_a_subscription_for_nothing_is_refused(self):
        with self.assertRaises(services.SubscriptionError):
            services.create("sub-3", amount="0")


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class CreationTests(TestCase):
    """Creating one charges nobody."""

    def test_creating_a_subscription_bills_nothing(self):
        # The moment money starts moving should be a decision somebody made
        # rather than a side effect of a record being created.
        services.create("sub-1", amount="10.00", provider=providers.MANUAL)

        self.assertEqual(SubscriptionState.PENDING, services.summarise("sub-1").state)
        self.assertEqual(0, BillingAttempt.objects.count())
        self.assertEqual([], list(services.due()))

    def test_creating_it_twice_returns_the_first_one(self):
        # For a Feature that charges people, the difference between a retry and a
        # duplicate is the whole ball game.
        services.create("sub-1", amount="10.00", provider=providers.MANUAL)
        services.create("sub-1", amount="99.00", provider=providers.MANUAL)

        self.assertEqual(1, Subscription.objects.count())
        self.assertEqual(_d(10), services.summarise("sub-1").amount)

    def test_an_unknown_reference_is_named_rather_than_created(self):
        with self.assertRaises(services.UnknownSubscription):
            services.bill("no-such-subscription")


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class OrderSeamTests(TestCase):
    """What the store turns a paid period into."""

    def setUp(self):
        services.create("sub-1", amount="10.00", lines=[_line()], provider=providers.MANUAL)
        services.activate("sub-1")
        services.bill("sub-1")

    def test_a_paid_period_is_waiting_for_an_order(self):
        waiting = services.periods_awaiting_orders()

        self.assertEqual(1, len(waiting))
        self.assertEqual(1, waiting[0].sequence)

    def test_recording_the_order_takes_it_off_the_list(self):
        services.record_order("sub-1", 1, 5501)

        self.assertEqual([], services.periods_awaiting_orders())

    def test_recording_it_twice_does_not_send_a_second_box(self):
        # A store runs its generator from cron. A second run that made a second
        # order would send a shopper two boxes for one payment.
        first = services.record_order("sub-1", 1, 5501)
        again = services.record_order("sub-1", 1, 5502)

        self.assertEqual(first.pk, again.pk)
        self.assertEqual(5501, again.source_order_number)

    def test_an_unpaid_period_is_not_waiting_for_anything(self):
        services.create("sub-2", amount="10.00", provider=providers.API)
        services.activate("sub-2")
        services.bill("sub-2")

        self.assertEqual(["sub-1"], [period.subscription.reference for period in services.periods_awaiting_orders()])


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class WorkerTests(TestCase):
    """The two scheduled jobs."""

    def test_the_billing_worker_charges_what_is_due_and_nothing_else(self):
        services.create("due", amount="10.00", provider=providers.MANUAL)
        services.activate("due")
        services.create("paused", amount="10.00", provider=providers.MANUAL)
        services.activate("paused")
        services.pause("paused")

        counts = services.run_billing()

        self.assertEqual(1, counts["billed"])
        self.assertEqual(_d(10), services.summarise("due").paid_to_date)
        self.assertEqual(_d(0), services.summarise("paused").paid_to_date)

    def test_the_retry_worker_gives_up_after_the_configured_attempts(self):
        services.create("sub-1", amount="10.00", provider=providers.API)
        services.activate("sub-1")
        services.bill("sub-1")

        now = timezone.now()

        # Three attempts is the configured default, spaced 1, 3 and 7 days. The
        # last pass is the one that gives up: a period that has used its attempts
        # must still be reachable by the retry query, or it sits in past-due for
        # ever chased by nobody.
        for days in (2, 6, 14, 30):
            services.retry_failed(now=now + timedelta(days=days))

        summary = services.summarise("sub-1")

        # `unpaid`, not `past_due`: a merchant needs to tell "we are chasing
        # this" from "we have stopped".
        self.assertEqual(SubscriptionState.UNPAID, summary.state)
        self.assertIsNone(BillingPeriod.objects.get().retry_at)

    def test_both_workers_run_on_a_store_with_no_subscriptions(self):
        # A worker that raises on a quiet night is a cron entry that alerts every
        # night until somebody switches it off.
        self.assertEqual({"billed": 0, "failed": 0, "refused": 0}, services.run_billing())
        self.assertEqual({"recovered": 0, "failed": 0, "given_up": 0}, services.run_retries())


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class ConcurrentBillingTests(TransactionTestCase):
    """
    Two workers, one subscription, two connections.

    The same demonstration `advanced-inventory` makes for stock and
    `restaurant-operations` makes for slots, and on this Feature it is the one
    that matters most: the thing being prevented is a real person being charged
    twice for one month.

    Removing the `select_for_update` from `_locked()` does not make this pass —
    the unique constraint on the period still refuses the second charge, which is
    the entire reason the guarantee lives in the database rather than in the
    lock.
    """

    available_apps = None

    def setUp(self):
        services.create("sub-1", amount="10.00", provider=providers.MANUAL)
        services.activate("sub-1")

    def test_two_workers_racing_the_same_subscription_charge_it_once(self):
        start = threading.Barrier(2, timeout=10)
        outcomes: list[object] = []
        lock = threading.Lock()

        def attempt() -> None:
            try:
                start.wait()
                charge = services.bill("sub-1")

                with lock:
                    outcomes.append(charge.outcome)
            except services.SubscriptionError as refusal:
                with lock:
                    outcomes.append(refusal)
            finally:
                # Each thread has its own connection, and a test that left them
                # open would hang the teardown rather than fail.
                connection.close()

        threads = [threading.Thread(target=attempt) for _ in range(2)]

        for thread in threads:
            thread.start()

        for thread in threads:
            thread.join(timeout=20)

        succeeded = BillingAttempt.objects.filter(outcome=AttemptOutcome.SUCCEEDED).count()

        self.assertEqual(1, succeeded, f"Expected exactly one charge, got {outcomes}.")
        self.assertEqual(1, BillingPeriod.objects.count())
        self.assertEqual(_d(10), services.summarise("sub-1").paid_to_date)


@skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
class HealthTests(TestCase):
    """The check KNIGHT runs after installing this, on a store with nothing in it."""

    def test_an_empty_store_is_healthy(self):
        from knight_feature_subscriptions import checks

        self.assertTrue(checks.health())

    def test_the_health_check_charges_nobody(self):
        from knight_feature_subscriptions import checks

        checks.health()

        # A health check that could take money would be worse than no health
        # check at all.
        self.assertEqual(0, BillingAttempt.objects.count())

    def test_the_configuration_never_reports_a_secret_by_value(self):
        from knight_feature_subscriptions import config

        described = config.describe()

        self.assertIn("secretsPresent", described)
        self.assertNotIn("secrets", described)


class TheStoreTurnsPaidPeriodsIntoOrdersTests(TestCase):
    """
    The seam, from the store's side. Runs whether or not the Feature is present,
    because the command has to behave either way — the same shape as every other
    sync in this store, and for the same reason: a Feature may not write an
    order.
    """

    def test_the_command_reports_rather_than_failing(self):
        from io import StringIO

        from django.core.management import call_command

        out = StringIO()
        call_command("knight_generate_subscription_orders", stdout=out)
        output = out.getvalue()

        self.assertTrue(
            "not installed" in output or "Created" in output,
            f"The command finished without saying what it did: {output!r}",
        )

    @skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
    def test_a_paid_period_becomes_a_confirmed_order_priced_as_the_period_was(self):
        from io import StringIO

        from django.core.management import call_command

        from apps.orders.models import Order, OrderStatus

        services.create(
            "sub-1",
            amount="25.00",
            lines=[_line(unit_price="25.00")],
            display_name="Sam",
            email="sam@example.com",
            provider=providers.MANUAL,
        )
        services.activate("sub-1")
        services.bill("sub-1")

        # The price changes after the period was paid. The order must document
        # what the shopper actually paid, not what the thing costs today.
        services.create("sub-2", amount="99.00", provider=providers.MANUAL)

        call_command("knight_generate_subscription_orders", stdout=StringIO())

        order = Order.objects.get()

        self.assertEqual(OrderStatus.CONFIRMED, order.status)
        self.assertEqual(_d(25), order.total)
        self.assertEqual("Sam", order.party.display_name)

    @skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
    def test_a_second_run_does_not_send_a_second_box(self):
        from io import StringIO

        from django.core.management import call_command

        from apps.orders.models import Order

        services.create("sub-1", amount="10.00", lines=[_line()], provider=providers.MANUAL)
        services.activate("sub-1")
        services.bill("sub-1")

        call_command("knight_generate_subscription_orders", stdout=StringIO())
        call_command("knight_generate_subscription_orders", stdout=StringIO())

        self.assertEqual(1, Order.objects.count())

    @skipUnless(INSTALLED, "The subscriptions Feature is not installed.")
    def test_an_unpaid_period_generates_nothing(self):
        from io import StringIO

        from django.core.management import call_command

        from apps.orders.models import Order

        services.create("sub-1", amount="10.00", lines=[_line()], provider=providers.API)
        services.activate("sub-1")
        services.bill("sub-1")

        call_command("knight_generate_subscription_orders", stdout=StringIO())

        # Nobody paid, so nobody gets a box.
        self.assertEqual(0, Order.objects.count())
