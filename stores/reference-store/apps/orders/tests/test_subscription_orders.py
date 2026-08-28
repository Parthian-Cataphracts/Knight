"""
The billing loop, from the store's side.

A subscription service says a period has been paid and has no order; this store
makes the order and says which one it made. The store is the only party that may
create an order — a Feature that wrote one would be a Feature this store could
not uninstall — and these tests are about the store keeping its half of that
bargain without making a second order for a payment that was taken once.

The service is faked at the HTTP boundary rather than at the function that calls
it, so what runs here is the real signed request: the store's own
`external.call`, its signature, and the paths it builds. A test that patched the
call itself would prove the command's arithmetic and nothing about whether the
service would recognise what arrived.
"""

from __future__ import annotations

import json
from decimal import Decimal
from io import StringIO
from unittest import mock

from django.core.management import call_command
from django.test import TestCase

from apps.orders.models import Order, OrderStatus
from knight_integration.external.contract import ExternalContract

CONTRACT = ExternalContract(
    slug="subscriptions",
    version="2.1.0",
    base_url="https://subscriptions.knight.dev",
    auth="hmac-sha256",
    health_path="/healthz",
    secret_name="SUBSCRIPTIONS_SERVICE_SECRET",
    webhooks=[],
    api_proxies=[],
    ui_mounts=[],
)


def owed(reference="SUB-1", sequence=1, **overrides):
    """One period the service says it is owed an order for."""
    item = {
        "orderReference": f"{reference}#{sequence}",
        "reference": reference,
        "sequence": sequence,
        "currency": "IRR",
        "amount": "10.00",
        "startsOn": "2026-01-01",
        "endsOn": "2026-01-30",
        "settledAt": "2026-01-01T00:00:00+00:00",
        "shopper": {"id": 7, "displayName": "Ada", "email": "ada@example.com"},
        "lines": [
            {
                "sourceProductId": 41,
                "sourceVariantId": None,
                "sku": "COFFEE",
                "name": "Coffee",
                "quantity": 2,
                "unitPrice": "5.00",
            }
        ],
    }
    item.update(overrides)
    return item


class FakeService:
    """
    The subscriptions service, answering the two routes the loop uses.

    It records what it was asked, because half of what is being tested is that
    the store reports the order back against the *period* the service named
    rather than against a subscription and a guess.
    """

    def __init__(self, items, *, refuse_reports=False):
        self.items = items
        self.refuse_reports = refuse_reports
        self.reported: list[tuple[str, int]] = []
        self.calls: list[tuple[str, str]] = []

    def __call__(self, method, url, **kwargs):
        from urllib.parse import urlsplit

        path = urlsplit(url).path
        self.calls.append((method, path))

        if path.endswith("/awaiting-orders/"):
            return _Answer(200, {"items": self.items})

        if path.endswith("/order/"):
            if self.refuse_reports:
                return _Answer(503, {"detail": "the service is having a bad day"})

            body = json.loads(kwargs["data"].decode())
            self.reported.append((path, body["orderNumber"]))

            return _Answer(200, {"orderNumber": body["orderNumber"]})

        raise AssertionError(f"The store called something it should not have: {method} {path}")


class _Answer:
    def __init__(self, status_code, payload):
        self.status_code = status_code
        self.content = json.dumps(payload).encode()
        self.text = self.content.decode()
        self.headers = {"Content-Type": "application/json"}

    def json(self):
        return json.loads(self.content)


#: The 1.x package, forced absent.
#:
#: CI installs every Feature package into the store, so without this the command
#: would take its in-process path and these tests would quietly stop exercising
#: the service one. Which shape of the Feature is present is the one thing the
#: command decides for itself, so a test about the other shape has to say.
NOT_INSTALLED = mock.patch(
    "apps.orders.management.commands.knight_generate_subscription_orders.django_apps.is_installed",
    side_effect=lambda name, *args, **kwargs: False,
)


class SubscriptionOrderLoopTests(TestCase):
    def run_command(self, service, **options) -> str:
        out, err = StringIO(), StringIO()

        with NOT_INSTALLED, mock.patch.dict(
            "os.environ", {"SUBSCRIPTIONS_SERVICE_SECRET": "s3cret"}
        ):
            with mock.patch(
                "knight_integration.external.call.external_features", return_value=[CONTRACT]
            ):
                with mock.patch("requests.request", side_effect=service):
                    call_command(
                        "knight_generate_subscription_orders", stdout=out, stderr=err, **options
                    )

        return out.getvalue() + err.getvalue()

    # --- The order the store makes -----------------------------------------

    def test_a_paid_period_becomes_a_confirmed_order_priced_as_the_period_was(self):
        service = FakeService([owed()])

        self.run_command(service)

        order = Order.objects.get()
        # Confirmed, not pending: the money is already taken, and an order
        # waiting for payment would be a lie the rest of the store acts on.
        self.assertEqual(OrderStatus.CONFIRMED, order.status)
        self.assertEqual(Decimal("10.00"), order.total)

        item = order.items.get()
        self.assertEqual("Coffee", item.product_name)
        self.assertEqual(2, item.quantity)
        self.assertEqual(41, item.source_product_id)
        self.assertEqual("Ada", order.party.display_name)

    def test_the_order_carries_the_features_reference_untouched(self):
        service = FakeService([owed()])

        self.run_command(service)

        # Opaque to the store, and carried rather than read. It is what the
        # announcement of this order hands back, and the only thing that lets the
        # service match an order to the period it paid for.
        self.assertEqual("SUB-1#1", Order.objects.get().external_reference)

    def test_the_order_number_goes_back_against_the_period_that_was_named(self):
        service = FakeService([owed(sequence=1), owed(sequence=2)])

        self.run_command(service)

        numbers = sorted(order.number for order in Order.objects.all())
        self.assertEqual(
            [
                ("/api/v1/admin/SUB-1/periods/1/order/", numbers[0]),
                ("/api/v1/admin/SUB-1/periods/2/order/", numbers[1]),
            ],
            sorted(service.reported),
        )

    # --- Making one order, whatever happens ---------------------------------

    def test_running_twice_makes_one_order(self):
        service = FakeService([owed()])

        self.run_command(service)
        # The service has not been told to stop asking — a delivery still in
        # flight, or a merchant running the job twice in one morning.
        self.run_command(service)

        # A second order would send a shopper two boxes for one payment. This is
        # the assertion the whole command is arranged around.
        self.assertEqual(1, Order.objects.count())

    def test_an_order_the_service_was_not_told_about_is_reported_on_the_next_run(self):
        failing = FakeService([owed()], refuse_reports=True)
        output = self.run_command(failing)

        # The order exists and the service does not know. That is the state the
        # reference on the order is for.
        self.assertEqual(1, Order.objects.count())
        self.assertIn("was not told", output)

        working = FakeService([owed()])
        self.run_command(working)

        self.assertEqual(1, Order.objects.count())
        self.assertEqual(
            [("/api/v1/admin/SUB-1/periods/1/order/", Order.objects.get().number)],
            working.reported,
        )

    def test_a_dry_run_creates_nothing(self):
        service = FakeService([owed()])

        output = self.run_command(service, dry_run=True)

        self.assertEqual(0, Order.objects.count())
        self.assertEqual([], service.reported)
        self.assertIn("SUB-1#1", output)

    def test_a_period_with_no_lines_is_reported_rather_than_ordered(self):
        service = FakeService([owed(lines=[])])

        output = self.run_command(service)

        # Money taken for nothing named is a data problem worth saying out loud,
        # not an empty order worth creating.
        self.assertEqual(0, Order.objects.count())
        self.assertIn("no lines", output)

    # --- When the service is not there --------------------------------------

    def test_a_service_that_does_not_answer_places_nothing_and_says_so(self):
        import requests

        def refuse(method, url, **kwargs):
            raise requests.RequestException("connection refused")

        output = self.run_command(refuse)

        self.assertEqual(0, Order.objects.count())
        self.assertIn("did not answer", output)

    def test_a_store_without_the_feature_generates_nothing(self):
        out = StringIO()

        with NOT_INSTALLED, mock.patch(
            "knight_integration.external.call.external_features", return_value=[]
        ):
            call_command("knight_generate_subscription_orders", stdout=out)

        self.assertEqual(0, Order.objects.count())
        self.assertIn("not on this store", out.getvalue())

    def test_the_request_to_the_service_is_signed_and_asserts_the_store_itself(self):
        service = FakeService([owed()])

        self.run_command(service)

        captured = {}

        def capture(method, url, **kwargs):
            captured.update(kwargs.get("headers") or {})
            return _Answer(200, {"items": []})

        with NOT_INSTALLED, mock.patch.dict(
            "os.environ", {"SUBSCRIPTIONS_SERVICE_SECRET": "s3cret"}
        ):
            with mock.patch(
                "knight_integration.external.call.external_features", return_value=[CONTRACT]
            ):
                with mock.patch("requests.request", side_effect=capture):
                    call_command("knight_generate_subscription_orders", stdout=StringIO())

        self.assertIn("X-Knight-Signature", captured)
        # Nobody is asking. The store calls as itself, and asserting a shopper
        # here would be telling the service something untrue.
        self.assertEqual("staff", captured["X-Knight-Identity"])
        self.assertEqual("system", captured["X-Knight-Subject"])
