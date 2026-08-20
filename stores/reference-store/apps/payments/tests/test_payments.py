"""Parity tests for the ported payment domain."""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.db import IntegrityError, transaction
from django.test import TestCase

from apps.payments.models import (
    AttemptStatus,
    Payment,
    PaymentAttempt,
    PaymentMethod,
    PaymentStatus,
)


def a_payment(**overrides) -> Payment:
    fields = {
        "source_order_id": 1,
        "order_number": 1001,
        "amount": Decimal("250000"),
        "method": PaymentMethod.ONLINE,
    }
    fields.update(overrides)

    return Payment.objects.create(**fields)


class LifecycleTests(TestCase):
    def test_a_counter_payment_settles_directly(self):
        # Pay-on-fulfilment has no processing step: the money is handed over.
        payment = a_payment(method=PaymentMethod.ON_FULFILLMENT)
        payment.transition_to(PaymentStatus.SUCCEEDED, actor="counter")

        self.assertTrue(payment.is_settled)
        self.assertIsNotNone(payment.succeeded_at)

    def test_an_online_payment_may_fail_and_be_retried(self):
        payment = a_payment()
        payment.transition_to(PaymentStatus.PROCESSING)
        payment.transition_to(PaymentStatus.FAILED)
        payment.transition_to(PaymentStatus.PROCESSING)
        payment.transition_to(PaymentStatus.SUCCEEDED)

        self.assertTrue(payment.is_settled)

    def test_a_settled_payment_cannot_be_failed(self):
        # The bug this prevents: a retried webhook marks a settled payment as
        # failed, and a shopper who was charged is told they were not. Reversing
        # a settled payment is a refund — a different transaction.
        payment = a_payment()
        payment.transition_to(PaymentStatus.SUCCEEDED)

        with self.assertRaises(ValidationError):
            payment.transition_to(PaymentStatus.FAILED)

    def test_a_cancelled_payment_is_terminal(self):
        payment = a_payment()
        payment.transition_to(PaymentStatus.CANCELLED)

        with self.assertRaises(ValidationError):
            payment.transition_to(PaymentStatus.PROCESSING)

    def test_every_transition_is_recorded(self):
        payment = a_payment()
        payment.transition_to(PaymentStatus.PROCESSING, actor="gateway")
        payment.transition_to(PaymentStatus.SUCCEEDED, actor="gateway")

        history = list(payment.history.all())

        self.assertEqual([entry.to_status for entry in history],
                         [PaymentStatus.PROCESSING, PaymentStatus.SUCCEEDED])
        self.assertEqual(history[0].from_status, PaymentStatus.PENDING)

    def test_the_version_moves_on_every_transition(self):
        payment = a_payment()
        payment.transition_to(PaymentStatus.PROCESSING)

        self.assertEqual(payment.version, 2)

    def test_one_payment_per_order(self):
        a_payment(source_order_id=7)

        with self.assertRaises(IntegrityError), transaction.atomic():
            a_payment(source_order_id=7)


class AttemptTests(TestCase):
    def test_attempts_are_numbered_in_the_order_they_happened(self):
        # Two declines followed by a success is the answer to "why does my
        # statement show three transactions".
        payment = a_payment()

        first = payment.start_attempt(provider_key="zarinpal")
        first.fail(code="card_declined", message="Insufficient funds.")

        second = payment.start_attempt(provider_key="zarinpal")
        second.succeed(reference="A-1234")

        self.assertEqual([a.attempt_number for a in payment.attempts.all()], [1, 2])
        self.assertEqual(payment.attempts.first().status, AttemptStatus.FAILED)
        self.assertEqual(payment.attempts.last().provider_reference, "A-1234")

    def test_a_failed_attempt_is_kept(self):
        payment = a_payment()
        attempt = payment.start_attempt()
        attempt.fail(code="timeout", message="The provider did not answer.")

        attempt.refresh_from_db()

        self.assertEqual(attempt.failure_code, "timeout")
        self.assertIsNotNone(attempt.completed_at)

    def test_an_attempt_number_cannot_repeat(self):
        payment = a_payment()
        payment.start_attempt()

        with self.assertRaises(IntegrityError), transaction.atomic():
            PaymentAttempt.objects.create(payment=payment, attempt_number=1)

    def test_a_counter_payment_needs_no_provider(self):
        # The base store records payments it did not itself take.
        payment = a_payment(method=PaymentMethod.ON_FULFILLMENT)
        attempt = payment.start_attempt()

        self.assertEqual(attempt.provider_key, "")
