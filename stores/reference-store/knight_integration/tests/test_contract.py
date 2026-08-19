"""
The store's side of the shared contract.

KNIGHT validates what it sends against the same schema in its own suite. These
tests validate what this store sends and what it answers, so a field renamed on
either side fails on both.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import json
from unittest import mock

from django.test import Client, TestCase, override_settings
from django.utils import timezone

from knight_integration.errors.middleware import build_event
from knight_integration.events.reporter import DEPLOYMENT_COMPLETED
from knight_integration.features import entitlements
from knight_integration.health import signature as request_signature

from . import contract


class CanonicalFormTests(TestCase):
    """
    The strings that are signed, checked against the worked examples both sides
    share. This is the test that catches the disagreement a schema cannot: two
    implementations that agree on the payload and produce different bytes.
    """

    def test_entitlement_canonical_form_matches_the_shared_example(self):
        sample = contract.samples()["entitlementCanonicalForm"]

        self.assertEqual(sample["expected"], entitlements.canonicalise(sample["payload"]))

    def test_request_canonical_form_matches_the_shared_example(self):
        sample = contract.samples()["requestCanonicalForm"]
        payload = sample["payload"]

        self.assertEqual(
            sample["expected"],
            request_signature.canonicalise(
                payload["method"], payload["path"], payload["timestamp"], payload["nonce"]
            ),
        )

    def test_feature_order_does_not_depend_on_the_order_received(self):
        sample = contract.samples()["entitlementCanonicalForm"]
        reversed_payload = dict(sample["payload"])
        reversed_payload["features"] = list(reversed(sample["payload"]["features"]))

        self.assertEqual(sample["expected"], entitlements.canonicalise(reversed_payload))


class PayloadShapeTests(TestCase):
    """What this store sends KNIGHT, validated against the schema."""

    def test_an_error_event_matches_the_contract(self):
        try:
            raise ValueError("something went wrong")
        except ValueError as exc:
            event = build_event(None, exc)

        contract.assert_matches("errorEvent", event)
        contract.assert_matches(
            "errorIngestRequest",
            {"environment": "Development", "version": "1.0.0", "events": [event]},
        )

    def test_a_deployment_event_matches_the_contract(self):
        event = {
            "occurredAt": timezone.now().isoformat().replace("+00:00", "Z"),
            "type": DEPLOYMENT_COMPLETED,
            "severity": "Info",
            "summary": "Deployed 1.1.0.",
            "traceId": None,
            "payload": {"version": "1.1.0", "previousVersion": "1.0.0"},
        }

        contract.assert_matches("storeEvent", event)
        contract.assert_matches("eventIngestRequest", {"environment": "Development", "events": [event]})

    def test_a_heartbeat_matches_the_contract(self):
        from knight_integration.features.registry import installed_features
        from knight_integration.health import checks

        status, dependencies = checks.run_all()

        contract.assert_matches(
            "heartbeatRequest",
            {
                "environment": "Development",
                "status": status,
                "storeVersion": "1.0.0",
                "dependencies": dependencies,
                "features": list(installed_features()),
                "detail": None,
            },
        )


@override_settings(
    KNIGHT={
        "BASE_URL": "http://localhost:5008",
        "CLIENT_ID": "knight-test-0000",
        "CLIENT_SECRET": "secret",
        "ENVIRONMENT": "Development",
        "STORE_ID": "00000000-0000-0000-0000-000000000001",
        "STORE_VERSION": "1.0.0",
        "ERROR_REPORTING": False,
        "DOMAIN_VERIFICATION_TOKEN": "knight-verify-abc123",
        "REQUEST_SIGNATURE_SKEW_SECONDS": 300,
    }
)
class HealthEndpointTests(TestCase):
    """What KNIGHT reads from this store, and who is allowed to read it."""

    SIGNING_KEY = base64.b64encode(b"0" * 32).decode("ascii")

    def _signed_headers(self, path: str, method: str = "GET", timestamp: int | None = None) -> dict[str, str]:
        import time

        stamp = str(timestamp if timestamp is not None else int(time.time()))
        nonce = "abcdef0123456789abcdef01"
        canonical = request_signature.canonicalise(method, path, stamp, nonce)
        signature = base64.b64encode(
            hmac.new(base64.b64decode(self.SIGNING_KEY), canonical.encode(), hashlib.sha256).digest()
        ).decode("ascii")

        return {
            "HTTP_X_KNIGHT_STORE": "00000000-0000-0000-0000-000000000001",
            "HTTP_X_KNIGHT_TIMESTAMP": stamp,
            "HTTP_X_KNIGHT_NONCE": nonce,
            "HTTP_X_KNIGHT_SIGNATURE": signature,
            "HTTP_X_KNIGHT_SIGNATURE_VERSION": "1",
        }

    def _session(self):
        from knight_integration.auth import StoreSession

        return StoreSession(
            access_token="token",
            expires_at=9_999_999_999,
            store_id="00000000-0000-0000-0000-000000000001",
            environment="Development",
            entitlement_signing_key=self.SIGNING_KEY,
            integration_status="Connected",
            domain_verification_outstanding=False,
            domain_verification_token="",
            heartbeat_seconds=60,
            feature_refresh_seconds=300,
        )

    def test_health_answers_the_contract_shape_for_a_signed_request(self):
        path = "/api/knight/health"

        with mock.patch("knight_integration.health.signature.get_session", return_value=self._session()):
            response = Client().get(path, **self._signed_headers(path))

        self.assertEqual(200, response.status_code)
        contract.assert_matches("storeHealthResponse", json.loads(response.content))

    def test_health_refuses_an_unsigned_request(self):
        # The payload names versions, dependencies and installed features: useful
        # to an operator, and just as useful to somebody choosing what to attack.
        response = Client().get("/api/knight/health")

        self.assertEqual(401, response.status_code)

    def test_health_refuses_a_stale_signature(self):
        path = "/api/knight/health"
        headers = self._signed_headers(path, timestamp=1_000_000_000)

        with mock.patch("knight_integration.health.signature.get_session", return_value=self._session()):
            response = Client().get(path, **headers)

        self.assertEqual(401, response.status_code)

    def test_health_refuses_a_signature_from_another_key(self):
        path = "/api/knight/health"
        headers = self._signed_headers(path)
        headers["HTTP_X_KNIGHT_SIGNATURE"] = base64.b64encode(b"not the signature").decode("ascii")

        with mock.patch("knight_integration.health.signature.get_session", return_value=self._session()):
            response = Client().get(path, **headers)

        self.assertEqual(401, response.status_code)

    def test_the_domain_verification_token_is_served_unauthenticated(self):
        # Deliberately public: it is the bootstrap step, run before this store has
        # ever handshaken and therefore before it holds a key to verify anything.
        response = Client().get("/.well-known/knight-domain-verification")

        self.assertEqual(200, response.status_code)
        self.assertEqual("knight-verify-abc123", response.content.decode().strip())
