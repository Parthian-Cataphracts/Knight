"""
The store's own record of which feature packages are installed.

Written only by the installer, read by everything else. It is deliberately a
plain JSON file next to the installed packages rather than a database table, for
two reasons:

- The Django app registry has to be built from it at **startup**, before the
  database is necessarily reachable. A store that cannot boot because its
  database is slow is a worse failure than the one this file avoids.
- It has to survive being read by a human during an incident. "Which version is
  actually on this box" should be answerable with `cat`, not with a shell.

This file is the store's truth, not KNIGHT's. When the two disagree, that is
drift, and it is KNIGHT's job to notice — which it can only do because the store
reports what this file says rather than what KNIGHT expected to be here
(docs/feature-delivery.md §14).
"""

from __future__ import annotations

import json
import logging
import os
import tempfile
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

REGISTRY_FILENAME = "installed.json"

#: Bumped only if the on-disk shape changes incompatibly. Present from the start
#: so that a future installer can recognise a file it must not misread.
SCHEMA_VERSION = 1


@dataclass
class InstalledFeature:
    """One feature package present on this store."""

    slug: str
    version: str
    app_label: str
    installed_app: str
    digest: str
    installed_at: str

    #: False when the feature is present but must not serve — an entitlement
    #: lapsed. The code and its data stay exactly where they are.
    enabled: bool = True

    url_include: str | None = None
    url_prefix: str | None = None
    health_check: str | None = None
    config_version: int = 0

    #: Scheduled jobs this feature declared, as {name, entrypoint, schedule}.
    #: Recorded here so `knight_run_workers` can find them without importing
    #: every installed package to ask - a feature that fails to import must not
    #: stop the others from running.
    workers: list[dict[str, Any]] = field(default_factory=list)

    extra: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_dict(cls, raw: dict[str, Any]) -> "InstalledFeature":
        known = {key: raw.get(key) for key in cls.__dataclass_fields__ if key in raw}
        known.setdefault("slug", raw.get("slug", ""))
        known.setdefault("version", raw.get("version", ""))
        return cls(**{**_defaults(), **known})


def _defaults() -> dict[str, Any]:
    return {
        "slug": "",
        "version": "",
        "app_label": "",
        "installed_app": "",
        "digest": "",
        "installed_at": "",
        "enabled": True,
        "url_include": None,
        "url_prefix": None,
        "health_check": None,
        "config_version": 0,
        "workers": [],
        "extra": {},
    }


class InstallationRegistry:
    """
    Reads and writes the on-disk record.

    Every write is atomic: the new content goes to a temporary file in the same
    directory and is then renamed over the old one. A half-written registry would
    be a store that cannot start, and an installer is exactly the thing most
    likely to be interrupted — by a failed migration, a restart, or an operator
    who has seen enough.
    """

    def __init__(self, root: Path | str) -> None:
        self._root = Path(root)
        self._path = self._root / REGISTRY_FILENAME

    @property
    def path(self) -> Path:
        return self._path

    @property
    def root(self) -> Path:
        return self._root

    def load(self) -> dict[str, InstalledFeature]:
        """
        Everything currently installed, by slug.

        A missing file means nothing is installed, which is the correct answer on
        a fresh store rather than an error. A *corrupt* file is not treated the
        same way: guessing "nothing is installed" there would make the installer
        try to reinstall packages that are already on disk.
        """
        if not self._path.exists():
            return {}

        try:
            raw = json.loads(self._path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            raise RuntimeError(
                f"The feature registry at {self._path} could not be read: {exc}. "
                "Refusing to continue rather than assume this store has no features installed."
            ) from exc

        features = raw.get("features", {})
        return {slug: InstalledFeature.from_dict(entry) for slug, entry in features.items()}

    def save(self, features: dict[str, InstalledFeature]) -> None:
        document = {
            "schemaVersion": SCHEMA_VERSION,
            "features": {slug: asdict(feature) for slug, feature in sorted(features.items())},
        }

        self._root.mkdir(parents=True, exist_ok=True)

        handle = tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            dir=self._root,
            prefix=".installed-",
            suffix=".tmp",
            delete=False,
        )

        try:
            with handle:
                json.dump(document, handle, indent=2, sort_keys=True)
                handle.flush()

                # fsync before the rename: without it a power loss can leave the
                # rename durable and the contents not, which is the one outcome
                # atomic replacement is supposed to rule out.
                os.fsync(handle.fileno())

            os.replace(handle.name, self._path)
        except BaseException:
            Path(handle.name).unlink(missing_ok=True)
            raise

    def record(self, feature: InstalledFeature) -> None:
        features = self.load()
        features[feature.slug] = feature
        self.save(features)

    def remove(self, slug: str) -> None:
        features = self.load()
        if features.pop(slug, None) is not None:
            self.save(features)

    def set_enabled(self, slug: str, enabled: bool) -> None:
        """
        Switches a feature on or off without touching its files.

        This is what losing an entitlement does. The distinction from removal is
        the whole point: the code stays, the data stays, and a customer who
        renews next week finds everything where they left it.
        """
        features = self.load()
        feature = features.get(slug)

        if feature is None:
            raise KeyError(f"'{slug}' is not installed on this store.")

        feature.enabled = enabled
        self.save(features)

    def get(self, slug: str) -> InstalledFeature | None:
        return self.load().get(slug)

    def enabled_features(self) -> list[InstalledFeature]:
        return [feature for feature in self.load().values() if feature.enabled]


def get_registry(root: Path | str | None = None) -> InstallationRegistry:
    """The registry for this store, rooted where configuration says features live."""
    if root is None:
        from ..conf import get_settings

        root = get_settings().feature_root

    return InstallationRegistry(root)
