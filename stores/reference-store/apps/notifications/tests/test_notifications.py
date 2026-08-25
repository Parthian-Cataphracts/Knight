"""
Transactional notifications: what gets recorded, and what happens when mail fails.

The interesting cases here are the failures, because the whole point of the app
is that a store can answer "did the customer get it" afterwards.
"""

from decimal import Decimal

from django.core import mail
from django.test import TestCase, override_settings

from apps.notifications import services
from apps.notifications.models import Notification, NotificationKind, NotificationStatus

LOCMEM = "django.core.mail.backends.locmem.EmailBackend"


@override_settings(EMAIL_BACKEND=LOCMEM, DEFAULT_FROM_EMAIL="shop@example.test")
class NotifyTests(TestCase):
    def setUp(self):
        mail.outbox = []

    def test_an_order_confirmation_is_sent_and_recorded(self):
        result = services.notify(
            NotificationKind.ORDER_CONFIRMATION,
            recipient="shopper@example.test",
            context={
                "customer_name": "Sara",
                "order_reference": "A-1001",
                "total": Decimal("120000"),
                "lines": [{"quantity": 2, "name": "Coffee", "total": Decimal("120000")}],
            },
            source_order_id=1001,
        )

        self.assertTrue(result.sent)
        self.assertEqual(len(mail.outbox), 1)
        self.assertEqual(mail.outbox[0].to, ["shopper@example.test"])
        self.assertIn("A-1001", mail.outbox[0].body)
        self.assertIn("Sara", mail.outbox[0].body)

        notification = result.notification
        self.assertEqual(notification.status, NotificationStatus.SENT)
        self.assertIsNotNone(notification.sent_at)

    def test_the_body_is_stored_as_it_was_sent(self):
        # A template changes; what a shopper was actually told does not, and a
        # support conversation about an old order needs the old wording.
        result = services.notify(
            NotificationKind.ORDER_CONFIRMATION,
            recipient="shopper@example.test",
            context={"order_reference": "A-1002", "total": Decimal("50000")},
            source_order_id=1002,
        )

        self.assertEqual(result.notification.body, mail.outbox[0].body.strip())

    def test_one_order_gets_one_confirmation_however_many_times_it_is_asked_for(self):
        # A retried checkout must not mail twice: a shopper receiving two
        # confirmations for one order reads it as a double charge.
        first = services.notify(
            NotificationKind.ORDER_CONFIRMATION,
            recipient="shopper@example.test",
            context={"order_reference": "A-1003"},
            source_order_id=1003,
        )
        second = services.notify(
            NotificationKind.ORDER_CONFIRMATION,
            recipient="shopper@example.test",
            context={"order_reference": "A-1003"},
            source_order_id=1003,
        )

        self.assertTrue(first.sent)
        self.assertFalse(second.sent)
        self.assertTrue(second.duplicate)
        self.assertEqual(len(mail.outbox), 1)
        self.assertEqual(Notification.objects.filter(source_order_id=1003).count(), 1)

    def test_different_kinds_about_one_order_are_all_sent(self):
        # The constraint is per kind and order, not per order: a shopper should
        # hear about confirmation, payment and dispatch separately.
        for kind in (
            NotificationKind.ORDER_CONFIRMATION,
            NotificationKind.PAYMENT_CONFIRMATION,
            NotificationKind.ORDER_FULFILLED,
        ):
            services.notify(
                kind,
                recipient="shopper@example.test",
                context={"order_reference": "A-1004", "amount": Decimal("1000")},
                source_order_id=1004,
            )

        self.assertEqual(len(mail.outbox), 3)

    def test_a_password_reset_carries_the_link_untouched(self):
        # A reset link with anything appended to it is a reset link that does
        # not work.
        url = "https://shop.example.test/reset?token=abc123"

        services.notify(
            NotificationKind.PASSWORD_RESET,
            recipient="shopper@example.test",
            context={"reset_url": url, "expires_in_minutes": 30},
        )

        self.assertIn(url, mail.outbox[0].body)

    def test_a_notification_with_no_order_can_be_sent_repeatedly(self):
        # Password resets are not about an order, and somebody may legitimately
        # ask for two. The partial constraint has to let them through.
        for _ in range(3):
            services.notify(
                NotificationKind.PASSWORD_RESET,
                recipient="shopper@example.test",
                context={"reset_url": "https://shop.example.test/reset"},
            )

        self.assertEqual(len(mail.outbox), 3)


class MailFailureTests(TestCase):
    """
    A mail server being down must not fail a sale that already took money.
    """

    @override_settings(EMAIL_BACKEND="apps.notifications.tests.test_notifications.BrokenBackend")
    def test_a_failure_is_recorded_rather_than_raised(self):
        result = services.notify(
            NotificationKind.ORDER_CONFIRMATION,
            recipient="shopper@example.test",
            context={"order_reference": "A-1005"},
            source_order_id=1005,
        )

        self.assertFalse(result.sent)
        self.assertFalse(result.duplicate)

        notification = result.notification
        self.assertEqual(notification.status, NotificationStatus.FAILED)
        self.assertIn("refused", notification.error)
        self.assertIsNone(notification.sent_at)

    @override_settings(EMAIL_BACKEND="apps.notifications.tests.test_notifications.BrokenBackend")
    def test_what_failed_can_be_listed_afterwards(self):
        # The list an operator needs after an outage, rather than discovering it
        # through complaints.
        services.notify(
            NotificationKind.ORDER_CONFIRMATION,
            recipient="a@example.test",
            context={"order_reference": "A-1"},
            source_order_id=1,
        )
        services.notify(
            NotificationKind.ORDER_CONFIRMATION,
            recipient="b@example.test",
            context={"order_reference": "A-2"},
            source_order_id=2,
        )

        self.assertEqual(len(services.unsent()), 2)


class BrokenBackend:
    """A mail backend that behaves like a server refusing connections."""

    def __init__(self, *args, **kwargs):
        pass

    def send_messages(self, messages):
        raise ConnectionRefusedError("connection refused by the mail server")
