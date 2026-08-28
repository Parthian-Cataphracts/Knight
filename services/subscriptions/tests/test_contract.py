"""
The store contract: who this service will answer, and what it refuses.

Written as attacks rather than happy paths, because this is the only boundary
between the internet and a service that can cancel somebody's subscription. The
happy path is covered once at the end; everything before it is a way in that has
to be closed.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import time
import uuid

from django.test import Client, TestCase
from django.utils import timezone

from knightlink.models import SeenNonce, Store
from knightlink.signing import canonical_string, forget_old_nonces
from subscriptions.models import Subscription

SECRET = "a-shared-secret-for-one-store"


def sign(secret: str, method: str, path: str, body: bytes = b"", *, timestamp=None, nonce=None) -> dict:
    """The headers the reference store attaches. Built here independently."""
    timestamp = str(timestamp if timestamp is not None else int(time.time()))
    nonce = nonce or uuid.uuid4().hex
    message = canonical_string(method, path, timestamp, nonce, body)

    return {
        "HTTP_X_KNIGHT_TIMESTAMP": timestamp,
        "HTTP_X_KNIGHT_NONCE": nonce,
        "HTTP_X_KNIGHT_SIGNATURE": "sha256="
        + hmac.new(secret.encode(), message.encode(), hashlib.sha256).hexdigest(),
    }


#: What each store in these tests signs with, by slug.
#:
#: Kept here rather than read off the store, because a secret is a row with a
#: lifetime and there is deliberately no column to read. A test that could ask a
#: store for its secret would be testing something this service does not offer.
SECRETS: dict[str, str] = {}


def registered(slug: str, secret: str) -> Store:
    """
    A store this service will answer, with one secret it signs with.

    A helper rather than two lines inline, because a secret is a row with a
    lifetime since phase 24 and every test that needs a store needs one.
    """
    store = Store.objects.create(store_id=uuid.uuid4(), slug=slug, enabled=True)
    store.rotate_to(secret, issued_by="a test")
    SECRETS[slug] = secret

    return store


class ContractTests(TestCase):
    def setUp(self) -> None:
        self.client = Client()
        self.store = registered("camden-coffee", SECRET)
        self.other = registered("borough-books", "a-different-secret")

    def call(self, path="/hooks/order-placed", payload=None, *, store=None, secret=None, headers=None, method="POST"):
        store = store or self.store
        body = json.dumps(payload or {}).encode()

        extra = {
            "HTTP_X_KNIGHT_STORE": str(store.store_id),
            "HTTP_X_KNIGHT_IDENTITY": "customer",
            "HTTP_X_KNIGHT_SUBJECT": "1",
            **sign(secret or SECRETS[store.slug], method, path, body),
            **(headers or {}),
        }

        return self.client.generic(method, path, body, content_type="application/json", **extra)

    # --- Ways in that must be closed ------------------------------------

    def test_an_unsigned_request_is_refused(self):
        response = self.client.post("/hooks/order-placed", {}, content_type="application/json")

        self.assertEqual(401, response.status_code)
        self.assertEqual("signature.missing", response.json()["errorCode"])

    def test_a_signature_by_another_stores_secret_is_refused(self):
        response = self.call(secret="a-different-secret")

        # The most important test in this file. Every store's secret is its own,
        # so one store leaking theirs cannot become the ability to impersonate
        # any other.
        self.assertEqual(401, response.status_code)
        self.assertEqual("signature.invalid", response.json()["errorCode"])

    def test_a_body_altered_after_signing_is_refused(self):
        path = "/hooks/order-cancelled"
        original = json.dumps({"subscriptionReference": "SUB-1", "cancelSubscription": False}).encode()
        tampered = json.dumps({"subscriptionReference": "SUB-1", "cancelSubscription": True}).encode()

        extra = {
            "HTTP_X_KNIGHT_STORE": str(self.store.store_id),
            **sign(SECRET, "POST", path, original),
        }

        response = self.client.generic("POST", path, tampered, content_type="application/json", **extra)

        # Somebody in the middle turning "do not cancel" into "cancel". The body
        # is covered by the signature precisely so this is not possible.
        self.assertEqual(401, response.status_code)

    def test_a_stale_request_is_refused(self):
        response = self.call(headers=sign(SECRET, "POST", "/hooks/order-placed", b"{}", timestamp=int(time.time()) - 4000))

        self.assertEqual(401, response.status_code)
        self.assertEqual("timestamp.stale", response.json()["errorCode"])

    def test_a_replayed_request_is_refused_the_second_time(self):
        path = "/hooks/order-placed"
        body = json.dumps({}).encode()
        headers = {"HTTP_X_KNIGHT_STORE": str(self.store.store_id), **sign(SECRET, "POST", path, body)}

        first = self.client.generic("POST", path, body, content_type="application/json", **headers)
        second = self.client.generic("POST", path, body, content_type="application/json", **headers)

        # A captured request is valid for as long as its timestamp is, and
        # without this it could be sent a hundred more times.
        self.assertEqual(200, first.status_code)
        self.assertEqual(401, second.status_code)
        self.assertEqual("nonce.replayed", second.json()["errorCode"])

    def test_a_failed_signature_burns_no_nonce(self):
        nonce = uuid.uuid4().hex
        path = "/hooks/order-placed"
        body = b"{}"

        self.client.generic(
            "POST", path, body, content_type="application/json",
            HTTP_X_KNIGHT_STORE=str(self.store.store_id),
            **sign("wrong-secret", "POST", path, body, nonce=nonce),
        )

        # Otherwise anybody could exhaust a legitimate store's nonce space with
        # unsigned requests, and the store's real request would be refused as a
        # replay of an attack it had nothing to do with.
        self.assertFalse(SeenNonce.objects.filter(nonce=nonce).exists())

        good = self.client.generic(
            "POST", path, body, content_type="application/json",
            HTTP_X_KNIGHT_STORE=str(self.store.store_id),
            **sign(SECRET, "POST", path, body, nonce=nonce),
        )
        self.assertEqual(200, good.status_code)

    def test_a_disabled_store_is_refused(self):
        self.store.enabled = False
        self.store.save(update_fields=["enabled"])

        response = self.call()

        # An entitlement that lapsed stops this service answering as well as
        # stopping the store forwarding. Relying on the store alone would mean a
        # stale registration could still reach a Feature nobody pays for.
        self.assertEqual(401, response.status_code)
        self.assertEqual("store.unknown", response.json()["errorCode"])

    def test_a_store_id_that_is_not_even_a_uuid_is_refused_before_the_database(self):
        response = self.client.generic(
            "POST", "/hooks/order-placed", b"{}", content_type="application/json",
            HTTP_X_KNIGHT_STORE="'; drop table knight_store; --",
            **sign("anything", "POST", "/hooks/order-placed", b"{}"),
        )

        # Checked for shape before the database is asked, so this endpoint
        # cannot be used to probe what the query does with arbitrary text.
        self.assertEqual(401, response.status_code)
        self.assertEqual("store.unknown", response.json()["errorCode"])

    def test_an_unknown_store_gets_the_same_answer_as_a_bad_signature(self):
        response = self.client.generic(
            "POST", "/hooks/order-placed", b"{}", content_type="application/json",
            HTTP_X_KNIGHT_STORE=str(uuid.uuid4()),
            **sign("anything", "POST", "/hooks/order-placed", b"{}"),
        )

        # Same status, same shape. A caller must not be able to learn which
        # stores this service serves by watching the difference.
        self.assertEqual(401, response.status_code)

    def test_a_shopper_cannot_reach_a_staff_route(self):
        response = self.call(path="/api/v1/admin/", method="GET")

        # Enforced here as well as in the store's proxy. One of those two checks
        # is somebody else's code, and a mis-wired proxy must not be the only
        # thing between a shopper and a merchant's whole book.
        self.assertEqual(403, response.status_code)

    def test_healthz_needs_no_signature(self):
        response = self.client.get("/healthz")

        self.assertEqual(200, response.status_code)
        self.assertEqual("healthy", response.json()["status"])

    # --- And the happy path ---------------------------------------------

    def test_a_correctly_signed_request_is_answered(self):
        response = self.call(payload={"subscriptionReference": ""})

        self.assertEqual(200, response.status_code)
        self.assertTrue(response.json()["received"])

    def test_old_nonces_are_forgotten(self):
        from datetime import timedelta

        SeenNonce.objects.create(store=self.store, nonce="old", seen_at=timezone.now() - timedelta(days=2))
        SeenNonce.objects.create(store=self.store, nonce="fresh")

        self.assertEqual(1, forget_old_nonces())
        self.assertTrue(SeenNonce.objects.filter(nonce="fresh").exists())


class IsolationTests(TestCase):
    """
    One deployment, many shops, and no query that crosses between them.

    In 1.x each store had its own database and "this store's subscriptions" was
    the whole table. Getting this wrong here is not a bug, it is one merchant
    reading another's book.
    """

    def setUp(self) -> None:
        self.client = Client()
        self.first = registered("first", SECRET)
        self.second = registered("second", SECRET)

        from subscriptions import services

        for store in (self.first, self.second):
            services.create(
                store,
                "SUB-1",
                amount="10.00",
                shopper_id=7,
                lines=[{"sku": "COFFEE", "name": "Coffee", "quantity": 1, "unit_price": "10.00"}],
            )

    def get(self, path, store, identity="staff", subject="7"):
        extra = {
            "HTTP_X_KNIGHT_STORE": str(store.store_id),
            "HTTP_X_KNIGHT_IDENTITY": identity,
            "HTTP_X_KNIGHT_SUBJECT": subject,
            **sign(SECRETS[store.slug], "GET", path, b""),
        }
        return self.client.get(path, **extra)

    def test_two_stores_may_both_use_the_same_reference(self):
        # The change that made this service possible. A global unique index
        # would have made the second shop's first subscription fail to create.
        self.assertEqual(2, Subscription.objects.filter(reference="SUB-1").count())

    def test_a_store_sees_only_its_own(self):
        response = self.get("/api/v1/admin/", self.first)
        items = response.json()["items"]

        self.assertEqual(1, len(items))
        self.assertEqual("SUB-1", items[0]["reference"])

        # And it is genuinely a different row from the other store's.
        mine = Subscription.objects.get(store=self.first, reference="SUB-1")
        theirs = Subscription.objects.get(store=self.second, reference="SUB-1")
        self.assertNotEqual(mine.pk, theirs.pk)

    def test_a_shopper_sees_only_their_own(self):
        from subscriptions import services

        services.create(
            self.first,
            "SUB-2",
            amount="20.00",
            shopper_id=99,
            lines=[{"sku": "TEA", "name": "Tea", "quantity": 1, "unit_price": "20.00"}],
        )

        response = self.get("/api/v1/subscriptions/", self.first, identity="customer", subject="7")
        references = [item["reference"] for item in response.json()["items"]]

        # Scoped to the shopper the *store* asserted, not to one this service
        # was told about in a query parameter.
        self.assertEqual(["SUB-1"], references)

    def test_a_shopper_cannot_read_another_shoppers_subscription_by_reference(self):
        from subscriptions import services

        services.create(
            self.first,
            "SUB-2",
            amount="20.00",
            shopper_id=99,
            lines=[{"sku": "TEA", "name": "Tea", "quantity": 1, "unit_price": "20.00"}],
        )

        response = self.get("/api/v1/subscriptions/SUB-2/", self.first, identity="customer", subject="7")

        # 404 rather than 403, and the same answer as a reference that does not
        # exist: distinguishing them lets a shopper enumerate a merchant's book.
        self.assertEqual(404, response.status_code)
