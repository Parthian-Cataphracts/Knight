"""
Entitlement caching, signature verification, and what the store enforces when
KNIGHT cannot be reached.

These are the tests that matter commercially: a bug here either gives away paid
capabilities or takes away ones a customer is paying for, and neither shows up
as an error anywhere.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import time
from unittest import mock

from django.core.cache import cache
from django.test import SimpleTestCase, override_settings

from knight_integration.features import entitlements

SIGNING_KEY = base64.b64encode(b"k" * 32).decode("ascii")


def sign(payload: dict) -> str:
    return base64.b64encode(
        hmac.new(base64.b64decode(SIGNING_KEY), entitlements.canonicalise(payload).encode(), hashlib.sha256).digest()
    ).decode("ascii")


def payload(features: list[str], issued_at: float | None = None, stale_in: int = 600) -> dict:
    issued = issued_at if issued_at is not None else time.time()

    body = {
        "storeId": "9f1d3b0e-5c4a-4c1e-9b7a-2f6d8e0a1c34",
        "customerId": "3a2b1c0d-4e5f-6a7b-8c9d-0e1f2a3b4c5d",
        "environment": "Development",
        "issuedAt": int(issued),
        "staleAfter": int(issued + stale_in),
        "features": [
            {
                "featureId": "11111111-1111-1111-1111-111111111111",
                "slug": slug,
                "name": slug.title(),
                "source": "Plan",
                "grantedAt": int(issued),
                "expiresAt": None,
            }
            for slug in features
        ],
        "signatureVersion": "1",
    }

    body["signature"] = sign(body)
    return body


class SignatureTests(SimpleTestCase):
    def test_a_correctly_signed_payload_verifies(self):
        self.assertTrue(entitlements.verify(payload(["storefront"]), SIGNING_KEY))

    def test_a_tampered_feature_list_does_not_verify(self):
        body = payload(["storefront"])
        body["features"].append(
            {
                "featureId": "22222222-2222-2222-2222-222222222222",
                "slug": "loyalty",
                "name": "Loyalty",
                "source": "Grant",
                "grantedAt": int(time.time()),
                "expiresAt": None,
            }
        )

        # Adding a capability to a signed set is exactly the attack the signature
        # exists to stop.
        self.assertFalse(entitlements.verify(body, SIGNING_KEY))

    def test_a_payload_signed_with_another_key_does_not_verify(self):
        other = base64.b64encode(b"x" * 32).decode("ascii")

        self.assertFalse(entitlements.verify(payload(["storefront"]), other))

    def test_an_unknown_signature_version_is_refused(self):
        body = payload(["storefront"])
        body["signatureVersion"] = "2"

        # Verifying a newer canonicalisation with the old rule would be a guess.
        self.assertFalse(entitlements.verify(body, SIGNING_KEY))

    def test_a_payload_without_a_signature_does_not_verify(self):
        body = payload(["storefront"])
        del body["signature"]

        self.assertFalse(entitlements.verify(body, SIGNING_KEY))


@override_settings(
    KNIGHT={
        "BASE_URL": "http://localhost:5008",
        "CLIENT_ID": "knight-test-0000",
        "CLIENT_SECRET": "secret",
        "ENVIRONMENT": "Development",
        "STORE_VERSION": "1.0.0",
        "ENTITLEMENT_GRACE_SECONDS": 3600,
        "ERROR_REPORTING": False,
    }
)
class CacheAndFallbackTests(SimpleTestCase):
    def setUp(self) -> None:
        cache.clear()

    def _session(self):
        from knight_integration.auth import StoreSession

        return StoreSession(
            access_token="token",
            expires_at=time.time() + 1800,
            store_id="9f1d3b0e-5c4a-4c1e-9b7a-2f6d8e0a1c34",
            environment="Development",
            entitlement_signing_key=SIGNING_KEY,
            integration_status="Connected",
            domain_verification_outstanding=False,
            domain_verification_token="",
            heartbeat_seconds=60,
            feature_refresh_seconds=300,
        )

    def _refresh_with(self, body):
        with mock.patch("knight_integration.auth.get_session", return_value=self._session()), mock.patch(
            "knight_integration.client.KnightClient.fetch_entitlements", return_value=body
        ):
            return entitlements.refresh()

    def test_a_refreshed_set_is_enforced(self):
        result = self._refresh_with(payload(["storefront", "loyalty"]))

        self.assertEqual({"storefront", "loyalty"}, set(result.slugs))
        self.assertTrue(entitlements.is_enabled("loyalty"))

    def test_a_payload_that_does_not_verify_is_refused(self):
        body = payload(["storefront"])
        body["signature"] = base64.b64encode(b"wrong").decode("ascii")

        with self.assertRaises(ValueError):
            self._refresh_with(body)

    def test_a_fresh_cached_set_is_used_without_calling_knight(self):
        self._refresh_with(payload(["storefront", "loyalty"]))

        with mock.patch("knight_integration.features.entitlements.refresh") as refresh:
            result = entitlements.current()

        refresh.assert_not_called()
        self.assertEqual({"storefront", "loyalty"}, set(result.slugs))

    def test_the_last_known_good_set_is_enforced_while_knight_is_unreachable(self):
        from knight_integration.client import KnightUnavailable

        self._refresh_with(payload(["storefront", "loyalty"], stale_in=0))

        with mock.patch(
            "knight_integration.features.entitlements.refresh",
            side_effect=KnightUnavailable("down"),
        ):
            result = entitlements.current()

        # Still enforcing what the customer paid for, and saying where it came from.
        self.assertEqual("last-known-good", result.source)
        self.assertTrue(result.is_enabled("loyalty"))

    def test_past_the_grace_period_the_store_falls_back_to_the_minimum_safe_set(self):
        from knight_integration.client import KnightUnavailable

        stale = time.time() - 7200
        self._refresh_with(payload(["storefront", "loyalty"], issued_at=stale, stale_in=0))

        with mock.patch(
            "knight_integration.features.entitlements.refresh",
            side_effect=KnightUnavailable("down"),
        ):
            result = entitlements.current()

        # Never fails open on a paid capability, and never takes the storefront
        # down either.
        self.assertEqual("minimum-safe", result.source)
        self.assertFalse(result.is_enabled("loyalty"))
        self.assertTrue(result.is_enabled("storefront"))

    def test_with_no_cache_at_all_the_store_falls_back_to_the_minimum_safe_set(self):
        from knight_integration.client import KnightUnavailable

        with mock.patch(
            "knight_integration.features.entitlements.refresh",
            side_effect=KnightUnavailable("down"),
        ):
            result = entitlements.current()

        self.assertEqual("minimum-safe", result.source)
        self.assertEqual(set(entitlements.MINIMUM_SAFE_FEATURES), set(result.slugs))


class FacadeTests(SimpleTestCase):
    """
    Entitlement and installation are separate facts, and the façade keeps them
    that way (docs/README.md rule 10).
    """

    def test_available_requires_both_entitlement_and_installation(self):
        from knight_integration import features

        with mock.patch.object(features, "is_enabled", return_value=True), mock.patch.object(
            features, "is_installed", return_value=False
        ):
            self.assertFalse(features.is_available("analytics"))

    def test_require_raises_when_a_capability_is_not_entitled(self):
        from knight_integration import features

        with mock.patch.object(features, "is_enabled", return_value=False):
            with self.assertRaises(features.FeatureNotEntitled):
                features.require("loyalty")
