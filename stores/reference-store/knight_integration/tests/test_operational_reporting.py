"""
Telling KNIGHT about the failures that raise no exception.

Three of this architecture's failures are handled correctly and locally and are
invisible to anybody who is not reading this store's log: a delivery that used
every attempt, a Feature's service that did not answer, and a Feature with no
shared secret to sign with. The store keeps working through all three, which is
right, and is exactly why nobody finds out.

What is pinned here is that each of them is *reported*, that a store with no
control plane is not broken by trying, and that reporting can never take the
shop down — because the alternative to being told is not knowing, and the
alternative to a safe reporter is a shop that falls over telling somebody about
a shop that was fine.
"""

from __future__ import annotations

import shutil
import tempfile
from pathlib import Path
from unittest import mock

from django.test import TestCase, override_settings
from django.utils import timezone

from knight_integration.errors import operational
from knight_integration.external import delivery
from knight_integration.external.delivery import DeliveryState, WebhookDelivery
from knight_integration.installer.state import InstalledFeature, get_registry


class OperationalReportingTests(TestCase):
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="knight-operational-"))
        self.addCleanup(shutil.rmtree, self.root, ignore_errors=True)

        self.reported: list[dict] = []

    def reporting(self, *, registered: bool = True, enabled: bool = True):
        """A reporter that keeps what it was given, and a store that is registered."""
        settings = mock.Mock()
        settings.is_registered = registered
        settings.error_reporting = enabled

        reporter = mock.Mock()
        reporter.enqueue.side_effect = self.reported.append

        return (
            mock.patch("knight_integration.conf.get_settings", return_value=settings),
            mock.patch("knight_integration.errors.queue.reporter", return_value=reporter),
        )

    def report(self, *args, **kwargs) -> bool:
        conf, queue = self.reporting(**kwargs.pop("reporting", {}))

        with conf, queue:
            return operational.report(*args, **kwargs)

    # --- What is reported ---------------------------------------------------

    def test_a_failure_is_reported_under_its_own_kind(self):
        self.assertTrue(
            self.report(
                operational.SERVICE_UNREACHABLE,
                "subscriptions did not answer GET /api/v1/public/",
                feature="subscriptions",
            )
        )

        event = self.reported[0]

        # The kind goes where an exception's type goes, because that is what
        # KNIGHT groups on: every unreachable service across every store lands in
        # one group, which is what makes "this is happening again" a screen
        # rather than a search.
        self.assertEqual(operational.SERVICE_UNREACHABLE, event["exceptionType"])
        self.assertEqual("subscriptions", event["context"]["feature"])

    def test_it_claims_no_endpoint_and_no_status(self):
        self.report(operational.DEAD_LETTERED, "gave up", feature="subscriptions")

        event = self.reported[0]

        # This did not happen on a request anybody made. A plausible-looking
        # endpoint would put a route on an errors screen that never failed.
        self.assertIsNone(event["endpoint"])
        self.assertIsNone(event["statusCode"])
        self.assertEqual("", event["stackTrace"])

    def test_a_store_with_no_control_plane_reports_nothing_and_is_fine(self):
        self.assertFalse(
            self.report(
                operational.DEAD_LETTERED,
                "gave up",
                reporting={"registered": False},
            )
        )

        # A store runs perfectly well without KNIGHT. Not reporting is the
        # correct behaviour, not a degraded one.
        self.assertEqual([], self.reported)

    def test_reporting_never_raises(self):
        with mock.patch("knight_integration.conf.get_settings", side_effect=RuntimeError("no settings")):
            # The one thing this module must never do is become the reason a
            # shop is down while telling somebody the shop is fine.
            self.assertFalse(operational.report(operational.DEAD_LETTERED, "gave up"))

    # --- Where it is reported from -------------------------------------------

    @override_settings(KNIGHT_FEATURE_ROOT=None)
    def test_a_dead_lettered_delivery_reports_itself(self):
        get_registry(self.root).record(
            InstalledFeature(
                slug="subscriptions",
                version="2.1.0",
                app_label="",
                installed_app="",
                digest="d" * 64,
                installed_at=timezone.now().isoformat(),
                enabled=True,
                extra={
                    "architecture": "external_service",
                    "service": {"base_url": "https://subscriptions.knight.dev", "secret": "S"},
                    "webhooks": [{"event": "order.placed", "path": "/hooks/order-placed", "delivery": "at-least-once"}],
                },
            )
        )

        WebhookDelivery.objects.create(
            feature_slug="subscriptions",
            event="order.placed",
            url="https://subscriptions.knight.dev/hooks/order-placed",
            payload={"orderNumber": 1},
            guarantee="at-least-once",
            attempts=delivery.MAX_ATTEMPTS - 1,
            next_attempt_at=timezone.now(),
        )

        conf, queue = self.reporting()

        with conf, queue:
            counts = delivery.send_due(sender=lambda row: 503)

        self.assertEqual(1, counts["dead"])
        self.assertEqual(DeliveryState.DEAD, WebhookDelivery.objects.get().state)

        # The point of the whole file: the queue kept the dead letter, and until
        # this line the only way to find one was to run `knight_deliver
        # --dead-letters` and know to.
        kinds = [event["exceptionType"] for event in self.reported]
        self.assertIn(operational.DEAD_LETTERED, kinds)

        reported = next(event for event in self.reported if event["exceptionType"] == operational.DEAD_LETTERED)
        self.assertEqual("subscriptions", reported["context"]["feature"])
        self.assertEqual("order.placed", reported["context"]["event"])

    def test_a_dead_letter_can_be_replayed_once_the_service_is_back(self):
        from io import StringIO

        from django.core.management import call_command

        dead = WebhookDelivery.objects.create(
            feature_slug="subscriptions",
            event="order.placed",
            url="https://subscriptions.knight.dev/hooks/order-placed",
            payload={"orderNumber": 1},
            guarantee="at-least-once",
            state=DeliveryState.DEAD,
            attempts=delivery.MAX_ATTEMPTS,
            last_error="503",
            next_attempt_at=timezone.now(),
        )

        call_command("knight_deliver", "--replay", str(dead.pk), stdout=StringIO())
        row = WebhookDelivery.objects.get(pk=dead.pk)

        # A fresh decision to deliver rather than a continuation of the run that
        # gave up: leaving the counter at the maximum would mean the replay
        # dying on its first failure.
        self.assertEqual(DeliveryState.PENDING, row.state)
        self.assertEqual(0, row.attempts)

    def test_replaying_something_that_was_never_given_up_on_is_refused(self):
        from io import StringIO

        from django.core.management import call_command
        from django.core.management.base import CommandError

        with self.assertRaises(CommandError):
            call_command("knight_deliver", "--replay", "4242", stdout=StringIO())
