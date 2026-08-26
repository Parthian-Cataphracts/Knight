"""
`loyalty-rewards`, installed.

The first Feature in the catalogue that keeps a running balance, so most of what
is pinned here is arithmetic that must never drift: idempotency under a retried
checkout, oldest-lot-first consumption, expiry, and a refund that writes new
rows rather than editing old ones.

The balance is derived from the ledger everywhere, and several of these tests
exist to keep it that way — a stored balance beside a ledger is two sources of
truth that agree until the first crash.
"""

from datetime import timedelta
from decimal import Decimal
from unittest import skipUnless

from django.test import TestCase
from django.utils import timezone

from feature_tests.support import installed, require

APP = "knight_feature_loyalty_rewards"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_loyalty_rewards import services
    from knight_feature_loyalty_rewards.models import (
        Account,
        Programme,
        Tier,
        Transaction,
        TransactionKind,
    )


@skipUnless(INSTALLED, "The loyalty-rewards Feature is not installed.")
class LoyaltyTestCase(TestCase):
    """Shared setup: a running programme with the default ladder."""

    def setUp(self):
        services.ensure_default_tiers()
        self.programme = Programme.current()
        self.now = timezone.now()

    def _lot(self, subject: str, points: int, *, expires_in_days: int | None, order_id: int):
        """An earn lot placed directly, so an expiry can be chosen."""
        account = services.account_for(subject)
        account.lifetime_points += points
        account.save(update_fields=["lifetime_points"])

        return Transaction.objects.create(
            account=account,
            kind=TransactionKind.EARN,
            points=points,
            points_remaining=points,
            expires_at=None if expires_in_days is None else self.now + timedelta(days=expires_in_days),
            source_order_id=order_id,
        )


class EarningTests(LoyaltyTestCase):
    def test_points_follow_the_rate(self):
        movement = services.earn("vera", amount=Decimal("1000"), source_order_id=1)

        self.assertEqual(movement.points, 1000)
        self.assertTrue(movement.applied)

    def test_points_are_floored_rather_than_rounded_up(self):
        # Rounding up hands out points nobody paid for, and at scale that is a
        # liability on somebody's balance sheet.
        self.programme.points_per_currency_unit = Decimal("0.5")
        self.programme.save()

        self.assertEqual(services.earn("vera", amount=Decimal("9"), source_order_id=1).points, 4)

    def test_one_order_earns_once_however_many_times_it_is_submitted(self):
        # A retried checkout is ordinary. It must be neither an error the store
        # has to catch nor points the customer gets twice.
        first = services.earn("vera", amount=Decimal("1000"), source_order_id=7)
        second = services.earn("vera", amount=Decimal("1000"), source_order_id=7)

        self.assertTrue(first.applied)
        self.assertTrue(second.duplicate)
        self.assertFalse(second.applied)
        self.assertEqual(services.balance_of("vera").points, 1000)

    def test_a_duplicate_can_still_report_the_balance(self):
        # The regression: an IntegrityError marks the transaction broken in
        # PostgreSQL, so without a savepoint the duplicate branch cannot run a
        # single query and the harmless retry fails instead.
        services.earn("vera", amount=Decimal("1000"), source_order_id=7)
        duplicate = services.earn("vera", amount=Decimal("1000"), source_order_id=7)

        self.assertEqual(duplicate.balance, 1000)

    def test_a_paused_programme_awards_nothing(self):
        self.programme.is_active = False
        self.programme.save()

        self.assertEqual(services.earn("vera", amount=Decimal("1000"), source_order_id=1).points, 0)

    def test_a_tier_multiplier_applies_to_what_is_earned_next(self):
        # Gold starts at 5000 lifetime and multiplies by 1.5.
        services.earn("vera", amount=Decimal("6000"), source_order_id=1)
        self.assertEqual(services.balance_of("vera").tier_name, "Gold")

        second = services.earn("vera", amount=Decimal("1000"), source_order_id=2)

        self.assertEqual(second.points, 1500)

    def test_spending_nothing_earns_nothing(self):
        self.assertEqual(services.earn("vera", amount=Decimal("0"), source_order_id=1).points, 0)
        self.assertFalse(Transaction.objects.filter(kind=TransactionKind.EARN).exists())


class TierTests(LoyaltyTestCase):
    def test_the_ladder_is_climbed_on_lifetime_points(self):
        services.earn("vera", amount=Decimal("1200"), source_order_id=1)

        self.assertEqual(services.balance_of("vera").tier_name, "Silver")

    def test_spending_points_does_not_demote(self):
        # A customer who redeems has not become less loyal, and a tier that
        # drops when somebody spends teaches them not to.
        services.earn("vera", amount=Decimal("6000"), source_order_id=1)
        services.redeem("vera", points=5900, source_order_id=2)

        held = services.balance_of("vera")
        self.assertEqual(held.points, 100)
        self.assertEqual(held.tier_name, "Gold")

    def test_seeding_tiers_twice_creates_nothing(self):
        self.assertEqual(services.ensure_default_tiers(), 0)
        self.assertEqual(Tier.objects.count(), 3)


class RedemptionTests(LoyaltyTestCase):
    def setUp(self):
        super().setUp()
        services.earn("vera", amount=Decimal("5000"), source_order_id=1)

    def test_a_redemption_below_the_floor_is_refused(self):
        # Letting somebody spend three points costs more in support than it
        # returns in loyalty.
        with self.assertRaises(services.LoyaltyError) as caught:
            services.redeem("vera", points=5, source_order_id=2)

        self.assertIn("100", str(caught.exception))

    def test_spending_more_than_the_balance_is_refused_with_the_shortfall(self):
        with self.assertRaises(services.LoyaltyError) as caught:
            services.redeem("vera", points=6000, source_order_id=2)

        self.assertIn("1000", str(caught.exception))

    def test_a_refusal_leaves_the_balance_untouched(self):
        # Refuses rather than partially applying: a shopper who asked to spend
        # 6000 and got 5000 spent has been given an outcome nobody chose.
        with self.assertRaises(services.LoyaltyError):
            services.redeem("vera", points=6000, source_order_id=2)

        self.assertEqual(services.balance_of("vera").points, 5000)

    def test_one_order_redeems_once(self):
        services.redeem("vera", points=1000, source_order_id=2)
        duplicate = services.redeem("vera", points=1000, source_order_id=2)

        self.assertTrue(duplicate.duplicate)
        self.assertEqual(services.balance_of("vera").points, 4000)

    def test_a_paused_programme_refuses_redemption(self):
        self.programme.is_active = False
        self.programme.save()

        with self.assertRaises(services.LoyaltyError):
            services.redeem("vera", points=1000, source_order_id=2)


class LotConsumptionTests(LoyaltyTestCase):
    """
    Oldest expiry first — so what is about to lapse is spent first, and the
    store's liability falls rather than ageing.
    """

    def setUp(self):
        super().setUp()
        self.programme.minimum_redemption_points = 1
        self.programme.save()

        self._lot("ali", 100, expires_in_days=5, order_id=10)
        self._lot("ali", 200, expires_in_days=40, order_id=11)
        self._lot("ali", 300, expires_in_days=400, order_id=12)

    def _remaining(self) -> list[int]:
        return list(
            Transaction.objects.filter(kind=TransactionKind.EARN)
            .order_by("expires_at")
            .values_list("points_remaining", flat=True)
        )

    def test_the_soonest_to_expire_is_spent_first(self):
        services.redeem("ali", points=150, source_order_id=99)

        self.assertEqual(self._remaining(), [0, 150, 300])

    def test_the_balance_is_the_sum_of_what_is_left(self):
        services.redeem("ali", points=150, source_order_id=99)

        self.assertEqual(services.balance_of("ali").points, 450)

    def test_what_is_about_to_expire_is_reported_separately(self):
        # Points nobody is told are about to expire are points that quietly
        # become a complaint.
        self.assertEqual(services.balance_of("ali", expiring_within_days=30).expiring_soon, 100)
        self.assertEqual(services.balance_of("ali", expiring_within_days=90).expiring_soon, 300)

    def test_a_lot_that_never_expires_is_spent_last(self):
        self._lot("nima", 50, expires_in_days=None, order_id=20)
        self._lot("nima", 50, expires_in_days=3, order_id=21)

        services.redeem("nima", points=50, source_order_id=22)

        expiring = Transaction.objects.get(source_order_id=21)
        forever = Transaction.objects.get(source_order_id=20)

        self.assertEqual(expiring.points_remaining, 0)
        self.assertEqual(forever.points_remaining, 50)


class ExpiryTests(LoyaltyTestCase):
    def setUp(self):
        super().setUp()
        self.programme.minimum_redemption_points = 1
        self.programme.save()

    def test_a_lapsed_lot_is_written_off(self):
        self._lot("ali", 77, expires_in_days=1, order_id=10)

        removed = services.expire_stale(at=self.now + timedelta(days=2))

        self.assertEqual(removed, 77)
        self.assertEqual(services.balance_of("ali").points, 0)

    def test_the_write_off_is_a_ledger_row_rather_than_a_deletion(self):
        # A ledger that can be edited answers no question worth asking.
        self._lot("ali", 77, expires_in_days=1, order_id=10)
        services.expire_stale(at=self.now + timedelta(days=2))

        written = Transaction.objects.get(kind=TransactionKind.EXPIRE)
        self.assertEqual(written.points, -77)
        self.assertTrue(Transaction.objects.filter(kind=TransactionKind.EARN).exists())

    def test_a_lot_that_has_not_lapsed_is_left_alone(self):
        self._lot("ali", 77, expires_in_days=40, order_id=10)

        self.assertEqual(services.expire_stale(at=self.now + timedelta(days=2)), 0)
        self.assertEqual(services.balance_of("ali").points, 77)

    def test_an_already_spent_lot_costs_nothing_to_expire(self):
        self._lot("ali", 100, expires_in_days=1, order_id=10)
        services.redeem("ali", points=100, source_order_id=11)

        self.assertEqual(services.expire_stale(at=self.now + timedelta(days=2)), 0)

    def test_the_sweep_is_idempotent(self):
        # A scheduled job that cannot safely be re-run is a job nobody can retry
        # after an outage.
        self._lot("ali", 77, expires_in_days=1, order_id=10)

        self.assertEqual(services.expire_stale(at=self.now + timedelta(days=2)), 77)
        self.assertEqual(services.expire_stale(at=self.now + timedelta(days=2)), 0)


class RefundTests(LoyaltyTestCase):
    def test_cancelling_an_order_takes_back_what_it_earned(self):
        services.earn("vera", amount=Decimal("1000"), source_order_id=1)

        services.refund("vera", source_order_id=1)

        self.assertEqual(services.balance_of("vera").points, 0)

    def test_points_already_spent_elsewhere_are_not_clawed_back(self):
        # Clawing them back would take a shopper's balance negative, which is a
        # number no loyalty programme should be able to produce.
        services.earn("vera", amount=Decimal("1000"), source_order_id=1)
        services.redeem("vera", points=600, source_order_id=2)

        services.refund("vera", source_order_id=1)

        self.assertEqual(services.balance_of("vera").points, 0)

    def test_cancelling_an_order_returns_what_it_spent(self):
        services.earn("vera", amount=Decimal("1000"), source_order_id=1)
        services.redeem("vera", points=600, source_order_id=2)

        services.refund("vera", source_order_id=2)

        self.assertEqual(services.balance_of("vera").points, 1000)

    def test_returned_points_get_a_fresh_expiry(self):
        # Reinstating the original expiry would sometimes hand a customer points
        # that expired while the store was deciding.
        self.programme.minimum_redemption_points = 1
        self.programme.save()
        self._lot("ali", 500, expires_in_days=2, order_id=10)
        services.redeem("ali", points=500, source_order_id=11)

        services.refund("ali", source_order_id=11)

        returned = Transaction.objects.filter(kind=TransactionKind.ADJUST, points__gt=0).first()
        self.assertGreater(returned.expires_at, self.now + timedelta(days=300))

    def test_a_refund_writes_rows_rather_than_deleting_them(self):
        services.earn("vera", amount=Decimal("1000"), source_order_id=1)
        before = Transaction.objects.count()

        services.refund("vera", source_order_id=1)

        self.assertGreater(Transaction.objects.count(), before)


class AdjustmentTests(LoyaltyTestCase):
    def test_staff_can_put_points_back_with_a_reason(self):
        movement = services.adjust("vera", points=500, reason="Goodwill after a late delivery")

        self.assertEqual(movement.points, 500)
        self.assertEqual(services.balance_of("vera").points, 500)

    def test_an_adjustment_needs_a_reason(self):
        # Exists so that putting points back is an audited transaction rather
        # than somebody editing a number.
        with self.assertRaises(services.LoyaltyError):
            services.adjust("vera", points=500, reason="   ")

    def test_an_adjustment_of_zero_is_refused(self):
        with self.assertRaises(services.LoyaltyError):
            services.adjust("vera", points=0, reason="nothing")

    def test_an_adjustment_cannot_take_a_balance_below_zero(self):
        services.earn("vera", amount=Decimal("100"), source_order_id=1)

        with self.assertRaises(services.LoyaltyError):
            services.adjust("vera", points=-500, reason="correction")


class BalanceDerivationTests(LoyaltyTestCase):
    def test_an_unknown_customer_has_an_empty_balance_rather_than_an_error(self):
        held = services.balance_of("nobody")

        self.assertTrue(held.is_empty)
        self.assertEqual(held.points, 0)
        self.assertEqual(held.lifetime_points, 0)

    def test_the_account_carries_no_balance_column(self):
        # The rule the whole Feature rests on. If a balance field ever appears
        # here, it is a second source of truth and this test should fail.
        fields = {field.name for field in Account._meta.get_fields()}

        self.assertNotIn("balance", fields)
        self.assertNotIn("points", fields)
        self.assertNotIn("points_balance", fields)

    def test_the_balance_survives_recomputation_from_the_ledger_alone(self):
        services.earn("vera", amount=Decimal("1000"), source_order_id=1)
        services.redeem("vera", points=400, source_order_id=2)

        from django.db.models import Sum

        remaining = (
            Transaction.objects.filter(
                account__subject="vera", kind=TransactionKind.EARN
            ).aggregate(total=Sum("points_remaining"))["total"]
            or 0
        )

        self.assertEqual(services.balance_of("vera").points, remaining)

    def test_an_expired_lot_is_already_out_of_the_balance_before_the_sweep(self):
        # A customer must never be shown points they cannot spend. The sweep is
        # bookkeeping that writes the ledger row; it is not what makes the
        # points stop counting.
        self._lot("ali", 77, expires_in_days=-1, order_id=10)

        self.assertEqual(services.balance_of("ali").points, 0)

    def test_what_points_are_worth_follows_the_programme(self):
        self.programme.currency_per_point = Decimal("0.05")
        self.programme.save()
        services.earn("vera", amount=Decimal("1000"), source_order_id=1)

        self.assertEqual(services.balance_of("vera").value, Decimal("50.00"))


class DeliveredRoutesTests(LoyaltyTestCase):
    def setUp(self):
        super().setUp()
        services.earn("vera", amount=Decimal("1200"), source_order_id=1)

    def test_the_programme_is_readable_so_a_storefront_need_not_hard_code_it(self):
        payload = self.client.get("/loyalty-rewards/").json()

        self.assertTrue(payload["active"])
        self.assertEqual(len(payload["tiers"]), 3)

    def test_a_balance_is_mounted_under_the_declared_prefix(self):
        payload = self.client.get("/loyalty-rewards/vera/").json()

        self.assertEqual(payload["points"], 1200)
        self.assertEqual(payload["tier"], "Silver")

    def test_the_history_reads_as_a_ledger(self):
        services.redeem("vera", points=200, source_order_id=2)

        rows = self.client.get("/loyalty-rewards/vera/history/").json()["history"]

        self.assertEqual([row["points"] for row in rows], [-200, 1200])

    def test_an_unknown_customer_reads_as_empty_rather_than_missing(self):
        payload = self.client.get("/loyalty-rewards/nobody/").json()

        self.assertEqual(payload["points"], 0)


class HealthCheckTests(LoyaltyTestCase):
    def test_it_passes_on_a_working_install(self):
        from knight_feature_loyalty_rewards.checks import health

        self.assertTrue(health())

    def test_it_fails_when_the_balance_cannot_be_derived(self):
        # For a ledger feature the check has to prove the aggregate works, not
        # merely that rows can be counted — that aggregate is what every other
        # path depends on.
        from unittest.mock import patch

        from knight_feature_loyalty_rewards.checks import health

        with patch(
            "knight_feature_loyalty_rewards.services.balance_of",
            side_effect=Exception("relation does not exist"),
        ):
            self.assertFalse(health())


class ExpiryCommandTests(TestCase):
    """The scheduled half, which lives in the store because the manifest has no worker."""

    def test_the_command_reports_rather_than_failing(self):
        from io import StringIO

        from django.core.management import call_command

        out = StringIO()
        call_command("knight_expire_loyalty_points", stdout=out)
        output = out.getvalue()

        self.assertTrue(
            "nothing to expire" in output or "Expired" in output,
            f"The command finished without saying what it did: {output!r}",
        )

    @skipUnless(INSTALLED, "The loyalty-rewards Feature is not installed.")
    def test_a_dry_run_writes_nothing(self):
        from io import StringIO

        from django.core.management import call_command

        services.ensure_default_tiers()
        account = services.account_for("ali")
        account.lifetime_points = 77
        account.save()
        Transaction.objects.create(
            account=account,
            kind=TransactionKind.EARN,
            points=77,
            points_remaining=77,
            expires_at=timezone.now() - timedelta(days=1),
            source_order_id=1,
        )

        def state():
            return (
                Transaction.objects.get(source_order_id=1).points_remaining,
                Transaction.objects.filter(kind=TransactionKind.EXPIRE).count(),
            )

        # The balance is the wrong thing to watch here: an expired lot is
        # already excluded from it, so it reads zero whether or not the sweep
        # has run. What the dry run protects is the ledger.
        call_command("knight_expire_loyalty_points", "--dry-run", stdout=StringIO())

        self.assertEqual(state(), (77, 0))

        call_command("knight_expire_loyalty_points", stdout=StringIO())

        self.assertEqual(state(), (0, 1))
