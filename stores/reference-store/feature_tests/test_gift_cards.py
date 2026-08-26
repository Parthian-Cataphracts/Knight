"""
`gift-cards`, installed.

This is money, so the tests are about the ways money goes wrong: spending a card
twice, spending more than is on it, a refund that has to be auditable, and a
balance that must never be anything other than the sum of the ledger.

Several of these exist to keep a future change honest rather than to check
today's behaviour — the constraint that stops a double-spend, and the absence of
a balance column.
"""

from datetime import timedelta
from decimal import Decimal
from unittest import skipUnless

from django.test import TestCase
from django.utils import timezone

from feature_tests.support import installed, require

APP = "knight_feature_gift_cards"
INSTALLED = installed(APP)
require(APP)

if INSTALLED:  # pragma: no cover - guarded above
    from knight_feature_gift_cards import services
    from knight_feature_gift_cards.models import (
        CardStatus,
        CreditEntry,
        EntryKind,
        GiftCard,
        GiftCardEntry,
        generate_code,
        normalize_code,
    )


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class CodeTests(TestCase):
    def test_a_code_has_no_ambiguous_characters(self):
        # A card is read aloud, over the phone, and off a receipt. O/0 and I/1
        # are support calls.
        for _ in range(50):
            self.assertFalse(set(generate_code()) & set("O0I1LU"))

    def test_codes_do_not_repeat(self):
        # The code *is* the authorisation. Guessing one must be infeasible.
        self.assertEqual(len({generate_code() for _ in range(500)}), 500)

    def test_a_code_matches_however_it_was_typed(self):
        card = services.issue(50)

        for typed in (
            card.code,
            card.code.lower(),
            card.code.replace("-", ""),
            f"  {card.code}  ",
            card.code.replace("-", " "),
        ):
            with self.subTest(typed=typed):
                self.assertIsNotNone(services.find(typed))

    def test_normalising_a_blank_code_is_refused(self):
        from django.core.exceptions import ValidationError

        with self.assertRaises(ValidationError):
            normalize_code("   ")


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class IssuingTests(TestCase):
    def test_a_card_is_worth_what_it_was_sold_for(self):
        card = services.issue(50)

        self.assertEqual(services.balance(card.code).remaining, Decimal("50.00"))

    def test_the_opening_entry_is_written_with_the_card(self):
        # A card row without an opening entry has a zero balance and looks
        # depleted the moment it is handed over.
        card = services.issue(50)

        self.assertEqual(
            GiftCardEntry.objects.filter(card=card, kind=EntryKind.ISSUE).count(), 1
        )

    def test_a_card_for_nothing_is_refused(self):
        for amount in (0, -5):
            with self.subTest(amount=amount):
                with self.assertRaises(services.GiftCardError):
                    services.issue(amount)

    def test_an_unknown_code_has_no_balance_rather_than_a_zero_one(self):
        # "This card does not exist" and "this card is empty" are different
        # answers, and a shopper who mistyped needs the first.
        self.assertIsNone(services.balance("ACDE-FGHJ-KMNP-QRTV"))

    def test_amounts_are_decimal_and_never_float(self):
        card = services.issue(0.1 + 0.2)

        # 0.30, not 0.30000000000000004.
        self.assertEqual(services.balance(card.code).remaining, Decimal("0.30"))


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class RedemptionTests(TestCase):
    def setUp(self):
        self.card = services.issue(50)

    def test_a_card_pays_what_it_can_and_no_more(self):
        # The normal case for a gift card: 5.00 left against a 20.00 basket pays
        # 5.00 and the checkout collects the rest another way. Refusing outright
        # would make every partly-spent card unusable.
        services.redeem(self.card.code, 45, source_order_id=1)

        outcome = services.redeem(self.card.code, 100, source_order_id=2)

        self.assertEqual(outcome.applied, Decimal("5.00"))
        self.assertEqual(outcome.remaining, Decimal("0.00"))

    def test_one_order_spends_a_card_once(self):
        # A retried checkout must not spend the card twice, and the constraint
        # is what settles it rather than a check the other request also passes.
        first = services.redeem(self.card.code, 20, source_order_id=1)
        second = services.redeem(self.card.code, 20, source_order_id=1)

        self.assertTrue(first.moved)
        self.assertTrue(second.duplicate)
        self.assertEqual(second.applied, Decimal("0.00"))
        self.assertEqual(services.balance(self.card.code).remaining, Decimal("30.00"))

    def test_a_duplicate_can_still_report_the_balance(self):
        # Without a savepoint around the insert, the IntegrityError leaves the
        # transaction broken and this branch cannot run a query at all.
        services.redeem(self.card.code, 20, source_order_id=1)

        self.assertEqual(
            services.redeem(self.card.code, 20, source_order_id=1).remaining, Decimal("30.00")
        )

    def test_a_balance_can_never_go_negative(self):
        # A card that can be overdrawn is a shop giving away money it was never
        # paid. Five orders each try to take 30 from a card worth 50: the first
        # two settle 30 and 20, and the rest are refused outright rather than
        # taking the balance below zero.
        settled = Decimal("0.00")
        refused = 0

        for order in range(1, 6):
            try:
                settled += services.redeem(self.card.code, 30, source_order_id=order).applied
            except services.GiftCardError:
                refused += 1

        self.assertEqual(settled, Decimal("50.00"))
        self.assertEqual(refused, 3)
        self.assertEqual(services.balance(self.card.code).remaining, Decimal("0.00"))

    def test_a_depleted_card_says_it_is_empty_rather_than_inactive(self):
        services.redeem(self.card.code, 50, source_order_id=1)

        self.assertEqual(services.balance(self.card.code).status, CardStatus.DEPLETED)

        with self.assertRaises(services.GiftCardError) as caught:
            services.redeem(self.card.code, 5, source_order_id=2)

        self.assertIn("no value left", str(caught.exception))

    def test_a_voided_card_says_it_is_not_active(self):
        services.void(self.card.code, reason="reported stolen")

        with self.assertRaises(services.GiftCardError) as caught:
            services.redeem(self.card.code, 5, source_order_id=1)

        self.assertIn("not active", str(caught.exception))

    def test_an_expired_card_says_it_has_expired(self):
        # Three refusals, three different messages. A shopper told the wrong one
        # takes the wrong next step.
        card = services.find(self.card.code)
        card.expires_at = timezone.now() - timedelta(days=1)
        card.save()

        with self.assertRaises(services.GiftCardError) as caught:
            services.redeem(self.card.code, 5, source_order_id=1)

        self.assertIn("expired", str(caught.exception))

    def test_an_unknown_code_is_refused_by_name(self):
        with self.assertRaises(services.GiftCardError):
            services.redeem("ACDE-FGHJ-KMNP-QRTV", 5, source_order_id=1)

    def test_redeeming_nothing_is_refused(self):
        for amount in (0, -5):
            with self.subTest(amount=amount):
                with self.assertRaises(services.GiftCardError):
                    services.redeem(self.card.code, amount, source_order_id=1)


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class RefundTests(TestCase):
    def setUp(self):
        self.card = services.issue(50)

    def test_cancelling_an_order_puts_the_value_back(self):
        services.redeem(self.card.code, 20, source_order_id=1)

        outcome = services.refund(self.card.code, source_order_id=1)

        self.assertEqual(outcome.applied, Decimal("20.00"))
        self.assertEqual(services.balance(self.card.code).remaining, Decimal("50.00"))

    def test_a_refund_revives_a_depleted_card(self):
        services.redeem(self.card.code, 50, source_order_id=1)
        self.assertEqual(services.balance(self.card.code).status, CardStatus.DEPLETED)

        services.refund(self.card.code, source_order_id=1)

        self.assertEqual(services.balance(self.card.code).status, CardStatus.ACTIVE)
        self.assertTrue(services.balance(self.card.code).redeemable)

    def test_a_refund_writes_an_entry_rather_than_deleting_the_redemption(self):
        # A money ledger that can be edited answers no question worth asking.
        services.redeem(self.card.code, 20, source_order_id=1)
        services.refund(self.card.code, source_order_id=1)

        self.assertTrue(
            GiftCardEntry.objects.filter(card=self.card, kind=EntryKind.REDEEM).exists()
        )
        self.assertTrue(
            GiftCardEntry.objects.filter(card=self.card, kind=EntryKind.REFUND).exists()
        )

    def test_refunding_an_order_that_never_spent_the_card_moves_nothing(self):
        outcome = services.refund(self.card.code, source_order_id=999)

        self.assertEqual(outcome.applied, Decimal("0.00"))
        self.assertEqual(services.balance(self.card.code).remaining, Decimal("50.00"))

    def test_refunding_twice_refunds_once(self):
        services.redeem(self.card.code, 20, source_order_id=1)
        services.refund(self.card.code, source_order_id=1)
        second = services.refund(self.card.code, source_order_id=1)

        self.assertTrue(second.duplicate)
        self.assertEqual(services.balance(self.card.code).remaining, Decimal("50.00"))


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class VoidTests(TestCase):
    def test_voiding_writes_the_remaining_value_off(self):
        # As auditable as the sale was: an entry, not a balance set to zero.
        card = services.issue(50)

        services.void(card.code, reason="bought with a stolen card")

        self.assertEqual(services.balance(card.code).remaining, Decimal("0.00"))
        self.assertTrue(GiftCardEntry.objects.filter(card=card, kind=EntryKind.VOID).exists())

    def test_voiding_needs_a_reason(self):
        card = services.issue(50)

        with self.assertRaises(services.GiftCardError):
            services.void(card.code, reason="  ")

    def test_voiding_twice_is_harmless(self):
        card = services.issue(50)
        services.void(card.code, reason="stolen")

        services.void(card.code, reason="stolen")

        self.assertEqual(
            GiftCardEntry.objects.filter(card=card, kind=EntryKind.VOID).count(), 1
        )


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class LiabilityTests(TestCase):
    def test_outstanding_value_is_what_the_shop_still_owes(self):
        # The number an accountant asks for, and the reason this is a ledger
        # rather than a counter.
        services.issue(50)
        second = services.issue(30)
        services.redeem(second.code, 10, source_order_id=1)

        self.assertEqual(services.outstanding(), Decimal("70.00"))

    def test_a_voided_card_is_no_longer_a_liability(self):
        card = services.issue(50)
        services.void(card.code, reason="issued in error")

        self.assertEqual(services.outstanding(), Decimal("0.00"))

    def test_liability_can_be_asked_for_one_currency(self):
        services.issue(50, currency="EUR")
        services.issue(40, currency="GBP")

        self.assertEqual(services.outstanding(currency="EUR"), Decimal("50.00"))
        self.assertEqual(services.outstanding(currency="GBP"), Decimal("40.00"))


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class LedgerIsTheTruthTests(TestCase):
    def test_the_card_carries_no_balance_column(self):
        # The rule the whole Feature rests on. If a balance field ever appears
        # here it is a second source of truth, and this test should fail.
        fields = {field.name for field in GiftCard._meta.get_fields()}

        self.assertNotIn("balance", fields)
        self.assertNotIn("remaining", fields)
        self.assertNotIn("remaining_amount", fields)

    def test_the_balance_is_exactly_the_sum_of_the_entries(self):
        from django.db.models import Sum

        card = services.issue(50)
        services.redeem(card.code, 20, source_order_id=1)
        services.refund(card.code, source_order_id=1)
        services.redeem(card.code, 15, source_order_id=2)

        summed = GiftCardEntry.objects.filter(card=card).aggregate(total=Sum("amount"))["total"]

        self.assertEqual(services.balance(card.code).remaining, summed)

    def test_the_history_reads_as_a_ledger(self):
        card = services.issue(50)
        services.redeem(card.code, 20, source_order_id=1)

        rows = services.history(card.code)

        self.assertEqual([row["kind"] for row in rows], [EntryKind.ISSUE, EntryKind.REDEEM])
        self.assertEqual([row["amount"] for row in rows], ["50.00", "-20.00"])


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class StoreCreditTests(TestCase):
    def test_credit_is_granted_with_a_reason_and_summed_from_the_ledger(self):
        balance = services.grant_credit("vera", 25, reason="Goodwill after a late delivery")

        self.assertEqual(balance, Decimal("25.00"))
        self.assertEqual(services.credit_balance("vera"), Decimal("25.00"))

    def test_a_grant_needs_a_reason(self):
        # Credit is a debt the shop takes on, and one that appeared with no
        # explanation is one nobody can reconcile.
        with self.assertRaises(services.GiftCardError):
            services.grant_credit("vera", 25, reason="   ")

    def test_credit_pays_what_it_can(self):
        services.grant_credit("vera", 10, reason="goodwill")

        outcome = services.spend_credit("vera", 40, source_order_id=1)

        self.assertEqual(outcome.applied, Decimal("10.00"))
        self.assertEqual(outcome.remaining, Decimal("0.00"))

    def test_one_order_spends_credit_once(self):
        services.grant_credit("vera", 50, reason="goodwill")
        services.spend_credit("vera", 20, source_order_id=1)

        duplicate = services.spend_credit("vera", 20, source_order_id=1)

        self.assertTrue(duplicate.duplicate)
        self.assertEqual(services.credit_balance("vera"), Decimal("30.00"))

    def test_spending_credit_nobody_has_is_refused(self):
        with self.assertRaises(services.GiftCardError):
            services.spend_credit("nobody", 10, source_order_id=1)

    def test_credit_is_returned_when_an_order_is_cancelled(self):
        services.grant_credit("vera", 50, reason="goodwill")
        services.spend_credit("vera", 20, source_order_id=1)

        services.refund_credit("vera", source_order_id=1)

        self.assertEqual(services.credit_balance("vera"), Decimal("50.00"))

    def test_credit_is_held_per_currency(self):
        # A store that changes its currency must not silently revalue what it
        # owes.
        services.grant_credit("vera", 50, currency="EUR", reason="goodwill")
        services.grant_credit("vera", 30, currency="GBP", reason="goodwill")

        self.assertEqual(services.credit_balance("vera", currency="EUR"), Decimal("50.00"))
        self.assertEqual(services.credit_balance("vera", currency="GBP"), Decimal("30.00"))

    def test_credit_and_cards_are_separate_ledgers(self):
        # They are different instruments: a card is a bearer token anybody
        # holding the code may spend, and credit belongs to one customer.
        services.grant_credit("vera", 25, reason="goodwill")
        services.issue(50)

        self.assertEqual(CreditEntry.objects.count(), 1)
        self.assertEqual(services.credit_balance("vera"), Decimal("25.00"))
        self.assertEqual(services.outstanding(), Decimal("50.00"))


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class DeliveredRoutesTests(TestCase):
    def test_the_check_route_is_mounted_under_the_declared_prefix(self):
        card = services.issue(50)

        payload = self.client.get(f"/gift-cards/check/?code={card.code}").json()

        self.assertEqual(payload["remaining"], "50.00")
        self.assertTrue(payload["redeemable"])

    def test_an_unknown_code_and_an_empty_card_answer_the_same_way(self):
        # A balance lookup by code is an oracle for guessing codes. Confirming
        # that a code exists but is empty is still information worth withholding.
        empty = services.issue(10)
        services.redeem(empty.code, 10, source_order_id=1)

        unknown = self.client.get("/gift-cards/check/?code=ACDE-FGHJ-KMNP-QRTV")
        depleted = self.client.get(f"/gift-cards/check/?code={empty.code}")

        self.assertEqual(unknown.status_code, 404)
        self.assertEqual(depleted.status_code, 404)
        self.assertEqual(unknown.json(), depleted.json())

    def test_the_check_route_says_nothing_about_who_the_card_was_for(self):
        card = services.issue(50, recipient_email="private@example.test", sender_name="Sara")

        body = self.client.get(f"/gift-cards/check/?code={card.code}").content.decode()

        self.assertNotIn("private@example.test", body)
        self.assertNotIn("Sara", body)

    def test_a_credit_balance_is_readable(self):
        services.grant_credit("vera", 25, reason="goodwill")

        payload = self.client.get("/gift-cards/credit/vera/").json()

        self.assertEqual(payload["balance"], "25.00")

    def test_an_unknown_customer_has_no_credit_rather_than_an_error(self):
        payload = self.client.get("/gift-cards/credit/nobody/").json()

        self.assertEqual(payload["balance"], "0.00")


@skipUnless(INSTALLED, "The gift-cards Feature is not installed.")
class HealthCheckTests(TestCase):
    def test_it_passes_on_a_working_install(self):
        from knight_feature_gift_cards.checks import health

        self.assertTrue(health())

    def test_it_fails_when_a_balance_cannot_be_derived(self):
        from unittest.mock import patch

        from knight_feature_gift_cards.checks import health

        with patch(
            "knight_feature_gift_cards.services.outstanding",
            side_effect=Exception("relation does not exist"),
        ):
            self.assertFalse(health())
