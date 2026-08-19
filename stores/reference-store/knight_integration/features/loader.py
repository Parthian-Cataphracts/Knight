"""
Bringing installed feature packages into the running Django project.

A feature is a normal Django app that arrived after the project was written, so
something has to add it to ``INSTALLED_APPS`` and mount its URLs. That is all
this module does, and it does it from the on-disk registry the installer writes
— never from KNIGHT, which is not reachable at import time and must not be on the
critical path of a store starting up.

Two rules shape everything here:

- **Only enabled features load.** A feature whose entitlement lapsed is still on
  disk with its data intact, and must not serve. Filtering at load time means the
  code is not merely told not to run — it is not wired up at all.
- **A broken feature must not stop the store.** A store that will not start
  because one optional feature has a bad import is a store that is entirely down
  over a capability the customer might not even use. Failures here are logged and
  skipped, loudly.
"""

from __future__ import annotations

import logging
import sys
from pathlib import Path

logger = logging.getLogger(__name__)


def feature_apps(feature_root: str | Path | None = None) -> list[str]:
    """
    The Django app paths of every enabled feature, for ``INSTALLED_APPS``.

    Called from settings, so it must not import Django models, touch the
    database, or raise. A store whose settings module explodes gives an operator
    a stack trace and no store.
    """
    try:
        features = _enabled(feature_root)
    except Exception:  # noqa: BLE001 - settings must never fail to import
        logger.exception("The feature registry could not be read; starting with base features only.")
        return []

    apps: list[str] = []

    for feature in features:
        if not feature.installed_app:
            logger.warning("Installed feature '%s' names no Django app; skipping.", feature.slug)
            continue

        apps.append(feature.installed_app)

    if apps:
        logger.info("Loading %s installed feature(s): %s", len(apps), ", ".join(apps))

    return apps


def ensure_import_path(feature_root: str | Path | None = None) -> None:
    """
    Puts the feature root on ``sys.path`` so installed packages are importable.

    Appended rather than prepended, deliberately. A feature package must never be
    able to shadow a module the store itself depends on: delivered code is
    trusted to be what KNIGHT published, not trusted to be careful about its
    names.
    """
    root = str(_root(feature_root))

    if root not in sys.path:
        sys.path.append(root)


def feature_urlpatterns(feature_root: str | Path | None = None):
    """
    URL patterns contributed by enabled features, for the project's root urlconf.

    Each feature is mounted under its declared prefix and imported in isolation:
    one feature with a bad urls module loses that feature, not the store's
    checkout page.
    """
    from django.urls import include, path

    patterns = []

    try:
        features = _enabled(feature_root)
    except Exception:  # noqa: BLE001
        logger.exception("The feature registry could not be read; mounting no feature URLs.")
        return patterns

    for feature in features:
        if not feature.url_include:
            continue

        prefix = (feature.url_prefix or f"{feature.slug}/").lstrip("/")

        try:
            patterns.append(path(prefix, include(feature.url_include)))
        except Exception:  # noqa: BLE001 - a bad urlconf loses one feature, not the store
            logger.exception("Feature '%s' has a urls module that could not be mounted.", feature.slug)

    return patterns


def installed_feature_report(feature_root: str | Path | None = None) -> list[dict[str, object]]:
    """
    What this store actually has installed, for the health payload KNIGHT reads.

    Reported from disk rather than from what KNIGHT believes, which is the whole
    mechanism by which drift becomes visible: if the two agree there is nothing
    to see, and if they disagree KNIGHT is the one that needs to know
    (docs/feature-delivery.md §14).
    """
    try:
        from ..installer.state import get_registry

        return [
            {
                "slug": feature.slug,
                "version": feature.version,
                "enabled": feature.enabled,
                "configVersion": feature.config_version,
            }
            for feature in get_registry(_root(feature_root)).load().values()
        ]
    except Exception:  # noqa: BLE001 - a health report must still be sent
        logger.exception("The feature registry could not be read for the health report.")
        return []


def _enabled(feature_root: str | Path | None):
    from ..installer.state import get_registry

    return get_registry(_root(feature_root)).enabled_features()


def _root(feature_root: str | Path | None) -> Path:
    if feature_root is not None:
        return Path(feature_root)

    from ..conf import get_settings

    return Path(get_settings().feature_root)
