"""
The billing loop: a paid period becoming an order in the store, and the store
saying which order it became.

The half of this Feature that was declared and never closed. The service knew a
period was owed an order and nothing turned that into one; the drill placed an
order by hand, which proves the event path and not the loop.

What is tested here is the seam, and the seam has exactly two rules:

- **the service never creates an order** — it names what is owed, and the store's
  own command creates it, because orders are the store's;
- **an order is matched to the period it paid for**, not to a guess. The
  reference the service hands out names the period, and that is what makes two
  orders created in one batch land on the right two periods when their
  deliveries arrive in the other order.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import time
import uuid

from django.test import Client, TestCase

from knightlink.signing import canonical_string
from tests.test_contract import registered
from subscriptions import services
from subscriptions.models import PeriodState, SubscriptionOrder

SECRET = "a-shared-secret-for-one-store"


def signed_headers(secret: str, method: str, path: str, body: bytes = b"") -> dict:
    timestamp = str(int(time.time()))
    nonce = uuid.uuid4().hex
    message = canonical_string(method, path, timestamp, nonce, body)

    return {
        "HTTP_X_KNIGHT_TIMESTAMP": timestamp,
        "HTTP_X_KNIGHT_NONCE": nonce,
        "HTTP_X_KNIGHT_SIGNATURE": "sha256="
        + hmac.new(secret.encode(), message.encode(), hashlib.sha256).hexdigest(),
    }


class OrderLoopTests(TestCase):
    def setUp(self) -> None:
        self.client = Client()
        self.store = registered("camden-coffee", SECRET)
        self.other = registered("borough-books", SECRET)

        services.create(
            self.store,
            "SUB-1",
            amount="10.00",
            shopper_id=7,
            display_name="Ada",
            email="ada@example.com",
            lines=[{"sku": "COFFEE", "name": "Coffee", "quantity": 2, "unit_price": "5.00"}],
        )
        services.create(
            self.other,
            "SUB-1",
            amount="10.00",
            lines=[{"sku": "BOOK", "name": "Book", "quantity": 1, "unit_price": "10.00"}],
        )

    # --- Helpers ----------------------------------------------------------

    def paid_period(self, store=None, reference="SUB-1", sequence=1):
        """A period in the one state the loop is about: charged, no order yet."""
        from datetime import date, timedelta

        from django.utils import timezone

        from subscriptions.models import BillingPeriod, Subscription

        subscription = Subscription.objects.get(store=store or self.store, reference=reference)
        starts = date(2026, 1, 1) + timedelta(days=30 * (sequence - 1))

        return BillingPeriod.objects.create(
            subscription=subscription,
            sequence=sequence,
            starts_on=starts,
            ends_on=starts + timedelta(days=29),
            currency="IRR",
            amount="10.00",
            state=PeriodState.PAID,
            settled_at=timezone.now(),
        )

    def get(self, path, *, store=None, identity="staff", subject="system"):
        store = store or self.store

        return self.client.get(
            path,
            HTTP_X_KNIGHT_STORE=str(store.store_id),
            HTTP_X_KNIGHT_IDENTITY=identity,
            HTTP_X_KNIGHT_SUBJECT=subject,
            **signed_headers(SECRET, "GET", path, b""),
        )

    def post(self, path, payload, *, store=None, identity="staff", subject="system"):
        store = store or self.store
        body = json.dumps(payload).encode()

        return self.client.post(
            path,
            body,
            content_type="application/json",
            HTTP_X_KNIGHT_STORE=str(store.store_id),
            HTTP_X_KNIGHT_IDENTITY=identity,
            HTTP_X_KNIGHT_SUBJECT=subject,
            **signed_headers(SECRET, "POST", path, body),
        )

    # --- What the store is told it owes ------------------------------------

    def test_a_paid_period_with_no_order_is_what_the_store_is_asked_for(self):
        self.paid_period()

        item = self.get("/api/v1/admin/awaiting-orders/").json()["items"][0]

        self.assertEqual("SUB-1#1", item["orderReference"])
        self.assertEqual(1, item["sequence"])
        # Everything the store needs to build the order, priced as the *period*
        # was: a shopper who subscribed at last year's price is owed last year's.
        self.assertEqual("10.00", item["amount"])
        self.assertEqual([{"name": "Coffee", "quantity": 2}],
                         [{"name": line["name"], "quantity": line["quantity"]} for line in item["lines"]])
        self.assertEqual("ada@example.com", item["shopper"]["email"])

    def test_a_period_that_already_has_an_order_is_not_asked_for_again(self):
        period = self.paid_period()
        services.record_order(self.store, "SUB-1", 1, 5001)

        self.assertEqual([], self.get("/api/v1/admin/awaiting-orders/").json()["items"])
        self.assertEqual(5001, period.order.source_order_number)

    def test_a_period_that_has_not_been_charged_is_not_asked_for(self):
        period = self.paid_period()
        period.state = PeriodState.PENDING
        period.save(update_fields=["state"])

        # An order for a period nobody has paid for is a box sent for nothing.
        self.assertEqual([], self.get("/api/v1/admin/awaiting-orders/").json()["items"])

    def test_a_store_is_never_told_about_another_stores_period(self):
        self.paid_period(store=self.other)

        # One deployment, many shops. Getting this wrong is not a bug, it is one
        # merchant being asked to ship another merchant's order.
        self.assertEqual([], self.get("/api/v1/admin/awaiting-orders/").json()["items"])

    def test_a_shopper_cannot_ask_what_the_store_owes(self):
        self.paid_period()

        response = self.get("/api/v1/admin/awaiting-orders/", identity="customer", subject="7")

        self.assertEqual(403, response.status_code)

    # --- The store saying which order it made -------------------------------

    def test_the_store_reports_the_order_and_the_period_is_settled(self):
        self.paid_period()

        response = self.post("/api/v1/admin/SUB-1/periods/1/order/", {"orderNumber": 5001})

        self.assertEqual(200, response.status_code)
        self.assertEqual(5001, response.json()["orderNumber"])
        self.assertEqual(5001, SubscriptionOrder.objects.get().source_order_number)

    def test_reporting_the_same_order_twice_makes_one_order(self):
        self.paid_period()

        self.post("/api/v1/admin/SUB-1/periods/1/order/", {"orderNumber": 5001})
        second = self.post("/api/v1/admin/SUB-1/periods/1/order/", {"orderNumber": 5001})

        # A merchant runs the generator from cron. A second run that made a
        # second order would send a shopper two boxes for one payment.
        self.assertEqual(200, second.status_code)
        self.assertEqual(1, SubscriptionOrder.objects.count())

    def test_a_second_different_order_for_one_period_is_refused(self):
        self.paid_period()

        self.post("/api/v1/admin/SUB-1/periods/1/order/", {"orderNumber": 5001})
        second = self.post("/api/v1/admin/SUB-1/periods/1/order/", {"orderNumber": 5002})

        self.assertEqual(409, second.status_code)
        self.assertEqual(1, SubscriptionOrder.objects.count())

    def test_one_order_number_cannot_be_two_periods(self):
        self.paid_period(sequence=1)
        self.paid_period(sequence=2)

        self.post("/api/v1/admin/SUB-1/periods/1/order/", {"orderNumber": 5001})
        clash = self.post("/api/v1/admin/SUB-1/periods/2/order/", {"orderNumber": 5001})

        # A 409 the store can read, rather than the unique constraint arriving
        # as a 500 that looks like this service is broken.
        self.assertEqual(409, clash.status_code)

    def test_a_store_cannot_report_an_order_against_another_stores_period(self):
        self.paid_period(store=self.other)

        response = self.post("/api/v1/admin/SUB-1/periods/1/order/", {"orderNumber": 5001})

        self.assertEqual(409, response.status_code)
        self.assertEqual(0, SubscriptionOrder.objects.count())

    def test_a_shopper_cannot_report_an_order(self):
        self.paid_period()

        response = self.post(
            "/api/v1/admin/SUB-1/periods/1/order/",
            {"orderNumber": 5001},
            identity="customer",
            subject="7",
        )

        self.assertEqual(403, response.status_code)

    # --- And the delivery that says the same thing --------------------------

    def test_the_announcement_lands_on_the_period_the_reference_names(self):
        first = self.paid_period(sequence=1)
        second = self.paid_period(sequence=2)

        # Two orders made in one batch, announced in the other order. Without a
        # reference naming the period, "the oldest owing one" would put each
        # order on the wrong month.
        self.post("/hooks/order-placed", {"externalReference": "SUB-1#2", "orderNumber": 5002})
        self.post("/hooks/order-placed", {"externalReference": "SUB-1#1", "orderNumber": 5001})

        self.assertEqual(5001, first.order.source_order_number)
        self.assertEqual(5002, second.order.source_order_number)

    def test_an_announcement_after_the_store_already_reported_changes_nothing(self):
        self.paid_period()
        self.post("/api/v1/admin/SUB-1/periods/1/order/", {"orderNumber": 5001})

        response = self.post(
            "/hooks/order-placed", {"externalReference": "SUB-1#1", "orderNumber": 5001}
        )

        # The synchronous report and the queued delivery say the same thing, and
        # at-least-once means both of them say it more than once.
        self.assertEqual(200, response.status_code)
        self.assertEqual(1, SubscriptionOrder.objects.count())

    def test_a_reference_naming_no_period_still_takes_the_oldest_owing_one(self):
        first = self.paid_period(sequence=1)

        # A merchant placing an order by hand against a subscription, or a store
        # on an older configuration. A guess, and the reason the generator does
        # not rely on one.
        self.post("/hooks/order-placed", {"externalReference": "SUB-1", "orderNumber": 5001})

        self.assertEqual(5001, first.order.source_order_number)

    def test_an_announcement_naming_a_period_that_was_never_paid_is_only_noted(self):
        period = self.paid_period()
        period.state = PeriodState.PENDING
        period.save(update_fields=["state"])

        response = self.post(
            "/hooks/order-placed", {"externalReference": "SUB-1#1", "orderNumber": 5001}
        )

        self.assertEqual(200, response.status_code)
        self.assertEqual(0, SubscriptionOrder.objects.count())
        self.assertEqual("noted", response.json()["action"])
