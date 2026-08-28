"""
The façade business code uses to ask about features.

This is the only part of knight_integration a business app may import. It
exposes two questions and hides everything behind them — the cache, the
signature, the fallback rules, and later the installed-package registry:

    from knight_integration.features import is_enabled, require

    if is_enabled("loyalty"):
        ...

Keeping the façade this thin is what lets the layer underneath change — a
different cache, a pushed rather than pulled set, real installed packages in
phase 3.5 — without a single business module noticing.

**Entitlement is not installation.** `is_enabled` answers the commercial
question: is this customer owed the capability. `is_installed` answers the
technical one. A feature that is entitled but not installed is a delivery gap
KNIGHT is told about, not something to serve; one that is installed but not
entitled must refuse (docs/README.md rule 10).
"""

from __future__ import annotations

from .entitlements import EntitlementSet, current, is_enabled, refresh
from .registry import installed_features, is_installed


class FeatureNotEntitled(RuntimeError):
    """Raised by :func:`require` when a capability is not paid for."""

    def __init__(self, slug: str) -> None:
        super().__init__(f"This store is not entitled to '{slug}'.")
        self.slug = slug


def require(slug: str) -> None:
    """
    Enforces an entitlement, raising when it is missing.

    For the paths where continuing without the capability would be wrong rather
    than merely reduced — a paid API endpoint, say, as opposed to a menu item
    that can simply be absent.
    """
    if not is_enabled(slug):
        raise FeatureNotEntitled(slug)


def is_available(slug: str) -> bool:
    """
    Whether the capability can actually be served: paid for *and* present.

    Until delivery exists (phase 3.5) every declared feature counts as present,
    so this tracks :func:`is_enabled`. Business code should still ask this
    question rather than the entitlement one, so that nothing has to change when
    installation becomes a real fact.
    """
    return is_enabled(slug) and is_installed(slug)


def announce(event: str, payload: dict) -> int:
    """
    Tells any Feature that subscribed that something happened in this store.

    On the façade rather than reached for directly, because a business module
    that imported the event bus would be coupled to how KNIGHT happens to
    deliver events today — which is the whole rule this façade exists to keep
    (`tests/test_boundaries.py`). Business code says what happened; nothing in
    `apps/` needs to know that a subscriber is an HTTP service, or that there is
    a queue, or that there are subscribers at all.

    Returns how many deliveries were queued, which is the useful thing to log.
    Zero is the overwhelmingly common answer and is not a failure.

    **Call it inside the transaction that made the thing true.** The delivery is
    written with it, so a rolled-back order takes its notifications with it.
    Nothing here touches the network (`docs/adr/0033-api-driven-features.md`).
    """
    from ..external import publish

    return publish(event, payload)


def known_events() -> frozenset[str]:
    """
    The events this store publishes, for a Feature to subscribe to.

    Exposed here so a business module can assert it is about to publish
    something real, without importing the catalogue itself.
    """
    from ..external import KNOWN_EVENTS

    return KNOWN_EVENTS


__all__ = [
    "EntitlementSet",
    "announce",
    "known_events",
    "FeatureNotEntitled",
    "current",
    "installed_features",
    "is_available",
    "is_enabled",
    "is_installed",
    "refresh",
    "require",
]
