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
    The Django app paths of every **installed** feature, for ``INSTALLED_APPS``.

    Installed rather than enabled, and the difference matters in three places.
    A feature is recorded before its migrations run, so an install that only
    loaded enabled apps could never migrate the thing it was installing. A
    disabled feature keeps its tables — that is what "disable, do not remove"
    means — and Django can only keep tables for an app it knows about. And an
    uninstall has to migrate those tables away, which needs the app loaded at the
    moment it is being removed.

    Enabled is about **serving**, and that is enforced where serving happens:
    `feature_urlpatterns` mounts routes for enabled features only.

    Called from settings, so it must not import Django models, touch the
    database, or raise. A store whose settings module explodes gives an operator
    a stack trace and no store.
    """
    try:
        features = _installed(feature_root)
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
    Puts every installed feature's package directory on ``sys.path``.

    The feature root itself **and one directory per feature**, because that is
    where the installer puts them: a delivered artifact is unpacked into
    ``<root>/<slug>/`` so that the previous version can be kept beside it and a
    rollback is a rename rather than a second download. The package inside is
    therefore one level deeper than the root, and a root-only path made every
    delivered Feature unimportable — `manage.py migrate` answered "No installed
    app with label 'knight_analytics_core'" for a package sitting on disk two
    directories away.

    Nothing noticed for six phases because every Feature in development is
    `pip install`ed into site-packages and registered with
    `knight_install_local`, which is the path that bypasses delivery entirely.
    Phase 18 installed the catalogue the way a customer does and found it at
    once.

    Appended rather than prepended, deliberately. A feature package must never be
    able to shadow a module the store itself depends on: delivered code is
    trusted to be what KNIGHT published, not trusted to be careful about its
    names. For the same reason the per-feature directories go after the root.
    """
    root = _root(feature_root)

    for candidate in [root, *_package_directories(root)]:
        entry = str(candidate)

        if entry not in sys.path:
            sys.path.append(entry)


def _package_directories(root: Path) -> list[Path]:
    """
    Each installed feature's own directory, for the features this store knows
    about.

    Read from the registry rather than by listing the directory, so a half-
    unpacked tree or a leftover from a failed install never ends up on the import
    path. A registry that cannot be read yields nothing: at this point in
    start-up there is no logging configured and no store to serve, and an
    exception here would stop the store rather than one feature.
    """
    try:
        from ..installer.state import get_registry

        installed = get_registry(root).load()
    except Exception:  # noqa: BLE001 - see the docstring
        return []

    return [root / slug for slug in sorted(installed) if (root / slug).is_dir()]


def feature_urlpatterns(feature_root: str | Path | None = None, existing=None):
    """
    URL patterns contributed by enabled features, for the project's root urlconf.

    Each feature is mounted under its declared prefix and imported in isolation:
    one feature with a bad urls module loses that feature, not the store's
    checkout page.

    Pass `existing` — the patterns the store has already registered — and a
    prefix that collides with one of them is reported. The store still wins,
    which is deliberate: a delivered package must not be able to take over a
    route the shop already serves. What is not acceptable is doing it in
    silence, because the result is a Feature whose install succeeded, whose
    health check passed, and whose pages answer somebody else's view.
    """
    from django.urls import include, path

    patterns = []

    try:
        features = _enabled(feature_root)
    except Exception:  # noqa: BLE001
        logger.exception("The feature registry could not be read; mounting no feature URLs.")
        return patterns

    taken = _declared_prefixes(existing)

    for feature in features:
        if not feature.url_include:
            continue

        prefix = (feature.url_prefix or f"{feature.slug}/").lstrip("/")

        if prefix in taken:
            logger.error(
                "Feature '%s' asks for the prefix '%s', which this store already serves. "
                "The store's own route wins, so this feature will not answer there. "
                "Change the prefix in its manifest.",
                feature.slug,
                prefix,
            )

        try:
            patterns.append(path(prefix, include(feature.url_include)))
        except Exception:  # noqa: BLE001 - a bad urlconf loses one feature, not the store
            logger.exception("Feature '%s' has a urls module that could not be mounted.", feature.slug)

    return patterns


def _declared_prefixes(existing) -> set[str]:
    """
    The route prefixes the store already serves, as far as they can be read.

    Best effort on purpose: this is a warning, not a gate, and a resolver shape
    it cannot read must not stop a store from starting.
    """
    if not existing:
        return set()

    prefixes: set[str] = set()

    for entry in existing:
        try:
            pattern = str(getattr(entry, "pattern", ""))
        except Exception:  # noqa: BLE001
            continue

        if not pattern:
            continue

        # An include() of the store's own urlconf mounted at "" contributes its
        # children's prefixes rather than its own.
        children = getattr(entry, "url_patterns", None)

        if pattern == "" and children:
            for child in children:
                child_pattern = str(getattr(child, "pattern", ""))

                if child_pattern:
                    prefixes.add(child_pattern.lstrip("/"))
            continue

        prefixes.add(pattern.lstrip("/"))

    return prefixes


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
    """The features that may serve requests."""
    from ..installer.state import get_registry

    return get_registry(_root(feature_root)).enabled_features()


def _installed(feature_root: str | Path | None):
    """Every feature whose code is on disk, serving or not. See `feature_apps`."""
    from ..installer.state import get_registry

    return list(get_registry(_root(feature_root)).load().values())


def _root(feature_root: str | Path | None) -> Path:
    if feature_root is not None:
        return Path(feature_root)

    from ..conf import get_settings

    return Path(get_settings().feature_root)
