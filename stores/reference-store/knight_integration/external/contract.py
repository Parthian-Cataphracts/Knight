"""
Reading what an external Feature declared, out of this store's own registry.

The contract is kept in the registry entry's ``extra`` map rather than in new
columns, which is why installing the first external Feature needed no change to
the registry format at all. A store that had been running for a year picks this
up on a redeploy without a migration.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from typing import Any

#: The value of ``extra["architecture"]`` for a Feature that is a service.
EXTERNAL = "external_service"


@dataclass(frozen=True)
class ExternalContract:
    """One external Feature's three lists, plus where its service is."""

    slug: str
    version: str
    base_url: str
    auth: str
    health_path: str
    secret_name: str
    webhooks: list[dict[str, Any]]
    api_proxies: list[dict[str, Any]]
    ui_mounts: list[dict[str, Any]]

    @classmethod
    def from_extra(cls, slug: str, version: str, extra: dict[str, Any]) -> "ExternalContract":
        service = extra.get("service") or {}

        return cls(
            slug=slug,
            version=version,
            base_url=str(service.get("base_url") or "").rstrip("/"),
            auth=str(service.get("auth") or "hmac-sha256"),
            health_path=str(service.get("health") or "/health"),
            secret_name=str(service.get("secret") or "KNIGHT_SERVICE_SECRET"),
            webhooks=list(extra.get("webhooks") or []),
            api_proxies=list(extra.get("api_proxies") or []),
            ui_mounts=list(extra.get("ui_mounts") or []),
        )

    def url_for(self, path: str) -> str:
        """An absolute URL on the service, from a path the manifest declared."""
        return f"{self.base_url}/{str(path).lstrip('/')}"


def contract_of(feature) -> ExternalContract | None:
    """The contract for one registry entry, or None when it is an ordinary package."""
    extra = getattr(feature, "extra", None) or {}

    if extra.get("architecture") != EXTERNAL:
        return None

    return ExternalContract.from_extra(feature.slug, feature.version, extra)


def external_features(feature_root: str | Path | None = None, *, enabled_only: bool = True) -> list[ExternalContract]:
    """
    Every external Feature this store holds.

    ``enabled_only`` by default, because everything that acts on these — the
    event bus, the proxy, the admin's menu — must respect an entitlement that
    has lapsed. Installed and enabled are separate facts and the store enforces
    both (docs/feature-delivery.md §2).
    """
    from ..installer.state import get_registry

    root = _root(feature_root)
    registry = get_registry(root)
    features = registry.enabled_features() if enabled_only else list(registry.load().values())

    return [contract for contract in map(contract_of, features) if contract is not None]


def _root(feature_root: str | Path | None) -> Path:
    if feature_root is not None:
        return Path(feature_root)

    from ..conf import get_settings

    return Path(get_settings().feature_root)
