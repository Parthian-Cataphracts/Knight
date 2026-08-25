"""
Shared guard for the Feature suites.

"Installed" means installed *as a Feature* — present in the store's feature
registry and therefore in INSTALLED_APPS — not merely importable. The two are
different states and only the first makes the models usable: a package pip has
put on the path but the installer never registered raises RuntimeError from the
model metaclass, not ImportError. Asking Django's app registry is the only check
that tells them apart.
"""

from __future__ import annotations

import os

from django.apps import apps as django_apps


def installed(app: str) -> bool:
    return django_apps.is_installed(app)


def require(*apps: str) -> None:
    """
    Refuses to let a suite skip when CI said it must run.

    A skipped Feature suite and a passing one look identical in a green run,
    which is how a release ships code nothing executed — the same reason the
    backend suite refuses to skip its PostgreSQL tests when
    REQUIRE_POSTGRES_TESTS is set.
    """
    if os.environ.get("REQUIRE_FEATURE_TESTS") != "1":
        return

    missing = [app for app in apps if not installed(app)]

    if missing:
        raise RuntimeError(
            "REQUIRE_FEATURE_TESTS=1 but these Features are not installed on this store: "
            + ", ".join(missing)
            + ". Register them with `manage.py knight_install_local` and pip install the "
            "packages before running the suite; letting these tests skip would report a "
            "pass for code nothing ran."
        )
