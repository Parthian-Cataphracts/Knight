"""
The store side of rotate-on-handshake (docs/hardening-backlog.md P2).

When KNIGHT hands back a replacement credential on a handshake, this store adopts
it and authenticates with it from the next handshake on — the half without which
a rotation would lock the store out when the old secret's grace ended.
"""

from __future__ import annotations

import tempfile
from unittest import mock

from django.test import TestCase, override_settings

from knight_integration import credentials
from knight_integration.conf import get_settings


def _knight(feature_root: str) -> dict:
    return {
        "BASE_URL": "http://localhost:5008",
        "CLIENT_ID": "knight-shop-oldoldoldold",
        "CLIENT_SECRET": "the-old-secret",
        "ENVIRONMENT": "Development",
        "STORE_ID": "00000000-0000-0000-0000-000000000001",
        "STORE_VERSION": "1.0.0",
        "ERROR_REPORTING": False,
        "FEATURE_ROOT": feature_root,
    }


class CredentialAdoptionTests(TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory()
        self.addCleanup(self._dir.cleanup)

    def test_the_environment_is_the_credential_until_one_is_rotated(self):
        with override_settings(KNIGHT=_knight(self._dir.name)):
            config = get_settings()

            self.assertIsNone(credentials.read_stored(config))
            self.assertEqual(
                ("knight-shop-oldoldoldold", "the-old-secret"),
                credentials.active_credential(config),
            )

    def test_a_rotated_credential_is_adopted_and_wins_over_the_environment(self):
        with override_settings(KNIGHT=_knight(self._dir.name)):
            config = get_settings()

            adopted = credentials.adopt_if_rotated(
                config,
                {
                    "accessToken": "a-token",
                    "rotatedCredential": {
                        "clientId": "knight-shop-newnewnewnew",
                        "clientSecret": "the-new-secret",
                        "expiresAt": "2027-01-01T00:00:00Z",
                    },
                },
            )

            self.assertTrue(adopted)

            # The next handshake authenticates with the replacement, not the
            # environment's original secret.
            self.assertEqual(
                ("knight-shop-newnewnewnew", "the-new-secret"),
                credentials.active_credential(config),
            )

    def test_a_handshake_without_a_rotation_adopts_nothing(self):
        with override_settings(KNIGHT=_knight(self._dir.name)):
            config = get_settings()

            self.assertFalse(credentials.adopt_if_rotated(config, {"accessToken": "a-token"}))
            self.assertFalse(credentials.adopt_if_rotated(config, {"accessToken": "a-token", "rotatedCredential": None}))
            self.assertIsNone(credentials.read_stored(config))

    def test_an_incomplete_rotation_is_ignored_rather_than_half_adopted(self):
        with override_settings(KNIGHT=_knight(self._dir.name)):
            config = get_settings()

            adopted = credentials.adopt_if_rotated(
                config,
                {"rotatedCredential": {"clientId": "knight-shop-newnewnewnew"}},
            )

            self.assertFalse(adopted)
            self.assertEqual(
                ("knight-shop-oldoldoldold", "the-old-secret"),
                credentials.active_credential(config),
            )

    def test_the_handshake_path_adopts_a_rotated_credential(self):
        response = {
            "accessToken": "a-token",
            "expiresIn": 1800,
            "storeId": "00000000-0000-0000-0000-000000000001",
            "environment": "Development",
            "entitlementSigningKey": "",
            "integrationStatus": "Connected",
            "heartbeatSeconds": 60,
            "featureRefreshSeconds": 300,
            "rotatedCredential": {"clientId": "knight-shop-newnewnewnew", "clientSecret": "the-new-secret"},
        }

        with override_settings(KNIGHT=_knight(self._dir.name)):
            from knight_integration import auth
            from knight_integration.client import KnightClient

            with mock.patch.object(KnightClient, "handshake", return_value=response):
                auth._handshake(get_settings())

            self.assertEqual(
                ("knight-shop-newnewnewnew", "the-new-secret"),
                credentials.active_credential(get_settings()),
            )
