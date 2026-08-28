"""
The control plane: who the stores are, what they may sign with, and when they
stop.

Written as attacks, like the store contract it sits beside, because this is the
one surface that can issue a credential. Everything a store can do wrong is a
subscription; everything that can go wrong here is somebody else becoming a
store.

The three facts these tests exist to pin:

- **rotation is not an outage.** Both secrets verify for the length of the
  window, so a request signed a second before the change is still good a second
  after it;
- **revocation is immediate, and it is this service's**, not the store's. A
  store whose registry is stale, wrong or restored from a backup is refused
  here;
- **KNIGHT is not a store.** It signs with its own secret, it gets no `Store`
  row, and its credential opens nothing that serves a store's data.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import time
import uuid
from datetime import timedelta

from django.test import Client, TestCase, override_settings
from django.utils import timezone

from knightlink.models import SeenNonce, Store, StoreSecret
from knightlink.signing import canonical_string

CONTROL_SECRET = "the-control-planes-own-secret-not-any-stores"
STORE_SECRET = "a-shared-secret-long-enough-to-be-accepted"


def signature_for(secret: str, method: str, path: str, body: bytes, *, timestamp=None, nonce=None) -> dict:
    timestamp = str(timestamp if timestamp is not None else int(time.time()))
    nonce = nonce or uuid.uuid4().hex
    message = canonical_string(method, path, timestamp, nonce, body)

    return {
        "HTTP_X_KNIGHT_TIMESTAMP": timestamp,
        "HTTP_X_KNIGHT_NONCE": nonce,
        "HTTP_X_KNIGHT_SIGNATURE": "sha256="
        + hmac.new(secret.encode(), message.encode(), hashlib.sha256).hexdigest(),
    }


@override_settings(KNIGHT_CONTROL_SECRET=CONTROL_SECRET)
class ControlPlaneTests(TestCase):
    def setUp(self) -> None:
        self.client = Client()
        self.store_id = str(uuid.uuid4())

    # --- Talking to KNIGHT's surface ---------------------------------------

    def knight(self, path, payload, *, secret=CONTROL_SECRET, **headers):
        body = json.dumps(payload).encode()

        return self.client.post(
            path,
            body,
            content_type="application/json",
            **{**signature_for(secret, "POST", path, body), **headers},
        )

    def register(self, **overrides):
        payload = {
            "storeId": self.store_id,
            "slug": "camden-coffee",
            "baseUrl": "https://camden.example.com",
            "secret": STORE_SECRET,
        }
        payload.update(overrides)

        return self.knight("/knight/stores/register", payload)

    def as_store(self, store, secret, path="/hooks/order-placed", payload=None):
        """One ordinary store request, signed with the secret under test."""
        body = json.dumps(payload or {}).encode()

        return self.client.post(
            path,
            body,
            content_type="application/json",
            HTTP_X_KNIGHT_STORE=str(store.store_id),
            HTTP_X_KNIGHT_IDENTITY="staff",
            HTTP_X_KNIGHT_SUBJECT="system",
            **signature_for(secret, "POST", path, body),
        )

    # --- Ways in that must be closed ---------------------------------------

    def test_an_unsigned_control_request_is_refused(self):
        response = self.client.post(
            "/knight/stores/register", {}, content_type="application/json"
        )

        self.assertEqual(401, response.status_code)
        self.assertEqual(0, Store.objects.count())

    def test_a_control_request_signed_with_a_stores_secret_is_refused(self):
        self.register()
        store = Store.objects.get()

        response = self.knight(
            "/knight/stores/rotate",
            {"storeId": self.store_id, "secret": "a-second-secret-long-enough-to-pass"},
            secret=STORE_SECRET,
        )

        # The most important test in this file. A store that could rotate its own
        # secret could rotate anybody's, and a store's secret is the one credential
        # in this architecture that is deliberately shared with somebody else.
        self.assertEqual(401, response.status_code)
        self.assertEqual(1, len(store.usable_secrets()))

    @override_settings(KNIGHT_CONTROL_SECRET="")
    def test_the_control_surface_is_closed_when_no_control_secret_is_set(self):
        response = self.knight("/knight/stores/register", {"storeId": self.store_id})

        # Unconfigured is refused, never open. The alternative is a service that
        # hands out credentials to anybody until somebody remembers to set a
        # variable.
        self.assertEqual(401, response.status_code)
        self.assertEqual("control.unconfigured", response.json()["errorCode"])

    def test_a_replayed_control_request_is_refused_the_second_time(self):
        body = json.dumps({"storeId": self.store_id, "slug": "camden", "secret": STORE_SECRET}).encode()
        headers = signature_for(CONTROL_SECRET, "POST", "/knight/stores/register", body)

        first = self.client.post(
            "/knight/stores/register", body, content_type="application/json", **headers
        )
        second = self.client.post(
            "/knight/stores/register", body, content_type="application/json", **headers
        )

        # Captured and sent again. The nonce table covers KNIGHT as well as the
        # stores, and it has to: this is the caller that can issue a secret.
        self.assertEqual(201, first.status_code)
        self.assertEqual(401, second.status_code)
        self.assertEqual("nonce.replayed", second.json()["errorCode"])

    def test_knight_gets_no_store_row_of_its_own(self):
        self.register()

        # One store, and it is the shop. A control plane with a `Store` row would
        # be a tenant with a tenant's access to somebody's subscriptions.
        self.assertEqual(["camden-coffee"], [store.slug for store in Store.objects.all()])
        self.assertTrue(SeenNonce.objects.filter(store__isnull=True).exists())

    def test_a_secret_that_is_too_short_is_refused(self):
        response = self.register(secret="short")

        self.assertEqual(400, response.status_code)
        self.assertEqual("secret.too_short", response.json()["errorCode"])
        self.assertEqual(0, Store.objects.count())

    def test_nothing_on_this_surface_returns_a_secret(self):
        self.register()

        described = self.knight("/knight/stores/describe", {"storeId": self.store_id}).json()

        # It reports how many are live, which is what a reconciliation needs, and
        # never a value. A control plane that could read a secret back would be a
        # second place it leaks from.
        self.assertEqual(1, described["usableSecrets"])
        self.assertNotIn(STORE_SECRET, json.dumps(described))

    # --- Registration -------------------------------------------------------

    def test_a_registered_store_can_immediately_sign_a_request(self):
        self.register()
        store = Store.objects.get()

        # The point of the whole surface: nobody typed anything on this host.
        self.assertEqual(200, self.as_store(store, STORE_SECRET).status_code)

    def test_registering_the_same_store_twice_is_one_store(self):
        self.assertEqual(201, self.register().status_code)
        self.assertEqual(200, self.register().status_code)

        # KNIGHT retries a call it is not sure arrived, and a retry that made a
        # second store — or a second secret — would be worse than the uncertainty.
        self.assertEqual(1, Store.objects.count())
        self.assertEqual(1, StoreSecret.objects.count())

    def test_a_rename_is_the_same_store(self):
        self.register()
        self.register(slug="camden-coffee-roasters")

        # By the id KNIGHT issued, not by the slug. A merchant renaming their shop
        # must not look like a new one.
        self.assertEqual(1, Store.objects.count())
        self.assertEqual("camden-coffee-roasters", Store.objects.get().slug)

    def test_registering_a_revoked_store_brings_it_back(self):
        self.register()
        self.knight("/knight/stores/revoke", {"storeId": self.store_id})

        self.register(secret="a-new-secret-issued-when-they-came-back")
        store = Store.objects.get()

        # Re-entitling a customer must not need somebody to remember a second
        # step on a second host.
        self.assertTrue(store.enabled)
        self.assertEqual(200, self.as_store(store, "a-new-secret-issued-when-they-came-back").status_code)

    # --- Rotation -----------------------------------------------------------

    def test_both_secrets_verify_during_the_overlap(self):
        self.register()
        self.knight(
            "/knight/stores/rotate",
            {"storeId": self.store_id, "secret": "the-new-secret-that-knight-just-issued", "overlapSeconds": 600},
        )

        store = Store.objects.get()

        # The gate for this phase. A request signed a second before the rotation
        # is still good a second after it, which is what makes rotating a secret
        # a deploy rather than an outage.
        self.assertEqual(200, self.as_store(store, STORE_SECRET).status_code)
        self.assertEqual(200, self.as_store(store, "the-new-secret-that-knight-just-issued").status_code)
        self.assertEqual(2, len(store.usable_secrets()))

    def test_the_old_secret_stops_working_when_the_window_closes(self):
        self.register()
        self.knight(
            "/knight/stores/rotate",
            {"storeId": self.store_id, "secret": "the-new-secret-that-knight-just-issued", "overlapSeconds": 600},
        )

        # The window, closed by moving the row rather than the clock: what is
        # being tested is that the expiry is honoured, not that time passes.
        StoreSecret.objects.filter(secret=STORE_SECRET).update(
            expires_at=timezone.now() - timedelta(seconds=1)
        )

        store = Store.objects.get()
        self.assertEqual(401, self.as_store(store, STORE_SECRET).status_code)
        self.assertEqual(200, self.as_store(store, "the-new-secret-that-knight-just-issued").status_code)

    def test_an_overlap_of_zero_ends_the_old_secret_at_once(self):
        self.register()
        self.knight(
            "/knight/stores/rotate",
            {"storeId": self.store_id, "secret": "the-secret-issued-after-a-leak-today", "overlapSeconds": 0},
        )

        store = Store.objects.get()

        # What a leak needs. Allowed, and not the default.
        self.assertEqual(401, self.as_store(store, STORE_SECRET).status_code)
        self.assertEqual(200, self.as_store(store, "the-secret-issued-after-a-leak-today").status_code)

    def test_rotating_to_the_same_secret_twice_issues_one(self):
        self.register()

        for _ in range(2):
            self.knight(
                "/knight/stores/rotate",
                {"storeId": self.store_id, "secret": "the-new-secret-that-knight-just-issued"},
            )

        self.assertEqual(2, StoreSecret.objects.count())

    def test_a_rotation_never_extends_a_secret_that_was_expiring(self):
        self.register()
        self.knight(
            "/knight/stores/rotate",
            {"storeId": self.store_id, "secret": "the-second-secret-knight-ever-issued", "overlapSeconds": 60},
        )
        first_expiry = StoreSecret.objects.get(secret=STORE_SECRET).expires_at

        self.knight(
            "/knight/stores/rotate",
            {"storeId": self.store_id, "secret": "the-third-secret-knight-ever-issued", "overlapSeconds": 3600},
        )

        # A second rotation with a longer window must not give the oldest secret
        # another hour of life. Rotation moves an expiry downwards only.
        self.assertEqual(first_expiry, StoreSecret.objects.get(secret=STORE_SECRET).expires_at)

    def test_rotating_an_unknown_store_is_a_404_and_creates_nothing(self):
        response = self.knight(
            "/knight/stores/rotate",
            {"storeId": str(uuid.uuid4()), "secret": "a-secret-for-a-store-that-is-not-here"},
        )

        self.assertEqual(404, response.status_code)
        self.assertEqual(0, Store.objects.count())

    # --- Revocation ---------------------------------------------------------

    def test_revoking_stops_the_next_request_from_that_store(self):
        self.register()
        store = Store.objects.get()

        self.assertEqual(200, self.as_store(store, STORE_SECRET).status_code)

        self.knight("/knight/stores/revoke", {"storeId": self.store_id})

        # The other half of the gate, and the half that was missing: the store
        # stops forwarding because its registry says so, and this stops the
        # service answering a store whose registry is stale or restored from a
        # backup.
        self.assertEqual(401, self.as_store(store, STORE_SECRET).status_code)

    def test_revoking_ends_the_secrets_as_well_as_disabling_the_store(self):
        self.register()

        self.knight("/knight/stores/revoke", {"storeId": self.store_id})
        store = Store.objects.get()

        # Two facts, because they answer different questions and an incident
        # wants both: disabled is "may not use this service", revoked is "this
        # key is not a key any more".
        self.assertFalse(store.enabled)
        self.assertEqual([], store.usable_secrets())

    def test_revoking_a_store_this_service_never_had_is_not_an_error(self):
        response = self.knight("/knight/stores/revoke", {"storeId": str(uuid.uuid4())})

        # KNIGHT withdrawing an entitlement from a store this service never knew
        # about has the outcome it wanted.
        self.assertEqual(200, response.status_code)
        self.assertFalse(response.json()["revoked"])

    def test_describing_an_unknown_store_says_so_rather_than_inventing_one(self):
        response = self.knight("/knight/stores/describe", {"storeId": str(uuid.uuid4())})

        self.assertEqual(404, response.status_code)
        self.assertFalse(response.json()["registered"])
        self.assertEqual(0, Store.objects.count())


class MaintenanceTests(TestCase):
    """
    The sweep that keeps two tables from growing for ever.

    Nonces are the replay defence and are only useful while a captured request's
    timestamp is still acceptable; spent secrets are history, and history is
    worth keeping without the part of it that is worth stealing.
    """

    def setUp(self) -> None:
        self.store = Store.objects.create(store_id=uuid.uuid4(), slug="camden-coffee")
        self.store.rotate_to(STORE_SECRET, issued_by="a test")

    def sweep(self) -> str:
        from io import StringIO

        from django.core.management import call_command

        out = StringIO()
        call_command("knight_maintain", stdout=out)

        return out.getvalue()

    def test_a_nonce_older_than_the_window_is_forgotten(self):
        SeenNonce.objects.create(store=self.store, nonce="old", seen_at=timezone.now() - timedelta(days=1))
        SeenNonce.objects.create(store=self.store, nonce="fresh")

        self.sweep()

        # Forgotten no earlier than the window that makes them matter: a nonce
        # dropped while its timestamp is still acceptable is a replay hole
        # exactly as wide as the difference.
        self.assertEqual(["fresh"], [seen.nonce for seen in SeenNonce.objects.all()])

    def test_a_long_revoked_secret_keeps_its_dates_and_loses_its_value(self):
        self.store.revoke_secrets(now=timezone.now() - timedelta(days=40))

        self.sweep()

        row = StoreSecret.objects.get()
        self.assertNotIn(STORE_SECRET, row.secret)
        # The row and its dates stay: "when did this key stop working" is the
        # first question of any incident.
        self.assertIsNotNone(row.revoked_at)

    def test_a_secret_still_inside_its_overlap_is_left_alone(self):
        self.store.rotate_to("the-secret-that-replaced-it", overlap_seconds=600)

        self.sweep()

        self.assertEqual(
            {STORE_SECRET, "the-secret-that-replaced-it"},
            {row.secret for row in StoreSecret.objects.all()},
        )

    def test_sweeping_twice_forgets_the_same_secret_once(self):
        self.store.revoke_secrets(now=timezone.now() - timedelta(days=40))

        self.sweep()
        second = self.sweep()

        # Safe to run at any interval, which is what makes it a cron entry
        # rather than an operator's decision.
        self.assertIn("blanked 0", second)
        self.assertEqual(1, StoreSecret.objects.count())
