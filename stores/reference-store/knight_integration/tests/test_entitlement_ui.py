"""
Phase 32B — the store's menu shows a Feature's UI only while it is entitled.

`visible_ui_mounts` is what a storefront/admin draws its Feature menu from, and it
must return a mount only for a Feature that is installed, enabled, and currently
entitled — so a lapsed entitlement removes the menu item, not merely the API
behind it.
"""

from __future__ import annotations

import tempfile
from unittest import mock

from django.test import TestCase

from knight_integration.features import visible_ui_mounts
from knight_integration.installer.state import InstalledFeature, get_registry


def _external(slug: str, *, enabled: bool) -> InstalledFeature:
    return InstalledFeature(
        slug=slug,
        version="2.1.0",
        app_label="",
        installed_app="",
        digest="sha256:x",
        installed_at="2026-01-01T00:00:00Z",
        enabled=enabled,
        extra={
            "architecture": "external_service",
            "service": {"base_url": "http://localhost:8100", "secret": "SUBSCRIPTIONS_SERVICE_SECRET"},
            "ui_mounts": [{"slot": "admin.sidebar", "label": "Subscriptions", "path": "/admin/subscriptions"}],
        },
    )


class VisibleUiMountsTests(TestCase):
    def setUp(self):
        self._dir = tempfile.TemporaryDirectory()
        self.addCleanup(self._dir.cleanup)
        self.root = self._dir.name

    def _install(self, *, enabled: bool):
        get_registry(self.root).record(_external("subscriptions", enabled=enabled))

    def test_an_entitled_enabled_feature_contributes_its_mount(self):
        self._install(enabled=True)

        with mock.patch("knight_integration.features.entitlements.is_enabled", return_value=True):
            mounts = visible_ui_mounts(self.root)

        self.assertEqual(len(mounts), 1)
        self.assertEqual(mounts[0]["slug"], "subscriptions")
        self.assertEqual(mounts[0]["slot"], "admin.sidebar")
        self.assertEqual(mounts[0]["path"], "/admin/subscriptions")

    def test_a_lapsed_entitlement_removes_the_mount(self):
        self._install(enabled=True)

        # The disable job has not landed yet (still enabled on disk), but the
        # entitlement is already gone — the menu must drop it now.
        with mock.patch("knight_integration.features.entitlements.is_enabled", return_value=False):
            mounts = visible_ui_mounts(self.root)

        self.assertEqual(mounts, [])

    def test_a_disabled_feature_is_not_shown_even_if_still_entitled(self):
        self._install(enabled=False)

        with mock.patch("knight_integration.features.entitlements.is_enabled", return_value=True):
            mounts = visible_ui_mounts(self.root)

        self.assertEqual(mounts, [])
