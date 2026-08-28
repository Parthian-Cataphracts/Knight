"""
The queue that makes ``at-least-once`` a guarantee.

Before this existed the bus resolved subscribers and handed each one to a
callable that logged and did nothing. A manifest could say ``at-least-once`` and
the store would lose the event the moment the service was slow.

The test that matters most is :meth:`QueueTests.test_an_event_survives_the_service_being_down`.
Everything else here is a property of the retry; that one is the phase's gate,
and it is the difference between a working queue and a lucky one.
"""

from __future__ import annotations

import shutil
import tempfile
from datetime import timedelta
from pathlib import Path
from unittest import mock

from django.test import TestCase
from django.utils import timezone

from knight_integration.external import delivery
from knight_integration.external.delivery import DeliveryState, WebhookDelivery
from knight_integration.installer.state import InstalledFeature, get_registry


class QueueTests(TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-delivery-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

        get_registry(self.root).record(
            InstalledFeature(
                slug="subscriptions",
                version="2.0.0",
                app_label="knight_subscriptions",
                installed_app="",
                digest="d" * 64,
                installed_at="2026-08-28T00:00:00+00:00",
                enabled=True,
                extra={
                    "architecture": "external_service",
                    "service": {
                        "base_url": "https://subscriptions.example.test",
                        "secret": "SUBSCRIPTIONS_SERVICE_SECRET",
                    },
                    "webhooks": [{"event": "order.placed", "path": "/hooks/order-placed"}],
                    "api_proxies": [],
                    "ui_mounts": [],
                },
            )
        )

        self.patched = mock.patch.dict(
            "os.environ", {"SUBSCRIPTIONS_SERVICE_SECRET": "s3cret"}
        )
        self.patched.start()
        self.addCleanup(self.patched.stop)

    def publish(self, event="order.placed", payload=None):
        from knight_integration.external import publish

        return publish(event, payload or {"orderNumber": 1001}, feature_root=self.root)

    # --- Queueing -------------------------------------------------------

    def test_publishing_writes_a_delivery_rather_than_sending_one(self):
        with mock.patch("requests.post") as post:
            self.assertEqual(1, self.publish())

        # Nothing on the network. A checkout that waited on a third party would
        # be a checkout that stops when they do.
        post.assert_not_called()

        row = WebhookDelivery.objects.get()
        self.assertEqual("order.placed", row.event)
        self.assertEqual(DeliveryState.PENDING, row.state)
        self.assertEqual("https://subscriptions.example.test/hooks/order-placed", row.url)

    def test_an_event_nobody_subscribed_to_queues_nothing(self):
        self.assertEqual(0, self.publish(event="product.created"))
        self.assertFalse(WebhookDelivery.objects.exists())

    def test_a_disabled_feature_is_queued_nothing(self):
        get_registry(self.root).set_enabled("subscriptions", False)

        self.assertEqual(0, self.publish())
        self.assertFalse(WebhookDelivery.objects.exists())

    # --- Sending --------------------------------------------------------

    def test_a_2xx_marks_it_delivered(self):
        self.publish()

        counts = delivery.send_due(sender=lambda row: 200)

        self.assertEqual(1, counts["delivered"])
        self.assertEqual(DeliveryState.DELIVERED, WebhookDelivery.objects.get().state)

    def test_a_500_is_retried_later_rather_than_dropped(self):
        self.publish()
        before = timezone.now()

        counts = delivery.send_due(sender=lambda row: 500)
        row = WebhookDelivery.objects.get()

        self.assertEqual(1, counts["retrying"])
        self.assertEqual(DeliveryState.PENDING, row.state)
        self.assertEqual(1, row.attempts)
        self.assertGreater(row.next_attempt_at, before)

    def test_a_400_is_also_retried_rather_than_dropped(self):
        self.publish()

        delivery.send_due(sender=lambda row: 400)

        # A service answering 400 to an event it asked for is a service with a
        # bug. Dropping the event silently would hide it; retrying and
        # eventually dead-lettering makes it somebody's problem.
        self.assertEqual(DeliveryState.PENDING, WebhookDelivery.objects.get().state)

    def test_it_is_given_up_on_after_the_last_attempt(self):
        self.publish()

        for _ in range(delivery.MAX_ATTEMPTS):
            row = WebhookDelivery.objects.get()
            row.next_attempt_at = timezone.now()
            row.save(update_fields=["next_attempt_at"])
            delivery.send_due(sender=lambda row: 503)

        row = WebhookDelivery.objects.get()

        # Kept, never deleted. A dead letter is the record that a Feature a
        # merchant pays for did not hear something.
        self.assertEqual(DeliveryState.DEAD, row.state)
        self.assertEqual(delivery.MAX_ATTEMPTS, row.attempts)

    def test_at_most_once_is_not_retried(self):
        get_registry(self.root).record(
            InstalledFeature(
                slug="advisory",
                version="1.0.0",
                app_label="advisory",
                installed_app="",
                digest="e" * 64,
                installed_at="2026-08-28T00:00:00+00:00",
                enabled=True,
                extra={
                    "architecture": "external_service",
                    "service": {"base_url": "https://advisory.example.test"},
                    "webhooks": [
                        {"event": "cart.abandoned", "path": "/hooks/cart", "delivery": "at-most-once"}
                    ],
                    "api_proxies": [],
                    "ui_mounts": [],
                },
            )
        )

        from knight_integration.external import publish

        publish("cart.abandoned", {"cartId": 1}, feature_root=self.root)
        delivery.send_due(sender=lambda row: 500)

        # The store tried and forgot, which is what the manifest asked for. Right
        # for something advisory and wrong for anything a customer is charged for.
        self.assertEqual(DeliveryState.DEAD, WebhookDelivery.objects.get(event="cart.abandoned").state)

    def test_nothing_is_attempted_before_its_time(self):
        self.publish()
        row = WebhookDelivery.objects.get()
        row.next_attempt_at = timezone.now() + timedelta(hours=1)
        row.save(update_fields=["next_attempt_at"])

        self.assertEqual(0, delivery.send_due(sender=lambda row: 200)["delivered"])

    def test_a_delivery_already_finished_is_not_sent_again(self):
        self.publish()
        delivery.send_due(sender=lambda row: 200)

        sent = []
        delivery.attempt(WebhookDelivery.objects.get(), sender=lambda row: sent.append(row) or 200)

        # Two workers racing to catch up after an outage is the obvious thing to
        # do, and it must not send everything twice.
        self.assertEqual([], sent)

    def test_a_feature_uninstalled_between_queueing_and_sending_is_not_called(self):
        self.publish()
        get_registry(self.root).set_enabled("subscriptions", False)

        with mock.patch(
            "knight_integration.external.delivery._contract_for", return_value=None
        ):
            delivery.send_due()

        row = WebhookDelivery.objects.get()

        # An entitlement that lapsed between queueing and sending must stop the
        # delivery. The URL is the queued one; whether we may still use it is a
        # question for now.
        self.assertEqual(DeliveryState.PENDING, row.state)
        self.assertIn("no longer installed", row.last_error)

    # --- The gate -------------------------------------------------------

    def test_an_event_survives_the_service_being_down(self):
        """
        The phase's gate, and the difference between a working queue and a lucky
        one.

        An order is placed while the service is unreachable. Nothing is lost:
        the delivery is queued, the attempt fails, it is retried later, and when
        the service comes back it arrives.
        """
        self.publish()

        # Down.
        def refuse(row):
            raise ConnectionError("connection refused")

        self.assertEqual(1, delivery.send_due(sender=refuse)["retrying"])

        row = WebhookDelivery.objects.get()
        self.assertEqual(DeliveryState.PENDING, row.state)
        self.assertIn("connection refused", row.last_error)

        # Back up, and the clock has come round.
        row.next_attempt_at = timezone.now()
        row.save(update_fields=["next_attempt_at"])

        received = []
        self.assertEqual(1, delivery.send_due(sender=lambda r: received.append(r.payload) or 200)["delivered"])

        self.assertEqual([{"orderNumber": 1001}], received)
        self.assertEqual(DeliveryState.DELIVERED, WebhookDelivery.objects.get().state)


class OrderAnnouncementTests(TestCase):
    """The store's own code saying what happened, through the façade."""

    def test_placing_an_order_announces_it(self):
        from knight_integration.features import announce, known_events

        self.assertIn("order.placed", known_events())

        with mock.patch("knight_integration.external.publish", return_value=0) as publish:
            announce("order.placed", {"orderNumber": 7})

        # Business code says what happened and knows nothing about subscribers,
        # queues or HTTP. That is the boundary `test_boundaries.py` enforces and
        # this is the other half of it.
        publish.assert_called_once()
