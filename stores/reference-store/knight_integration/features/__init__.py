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


def visible_ui_mounts(feature_root: str | None = None) -> list[dict]:
    """
    The UI mounts a store should render in its menu right now.

    A mount is shown only for a Feature that is installed, enabled, *and* still
    entitled — so a customer sees a control for exactly the Features they are
    paying for, and a lapsed entitlement takes the menu item with it on the next
    refresh rather than only refusing the API behind it (phase 32B,
    docs/authorization.md §5). Each mount carries its ``slug`` so the nav can key
    on it.

    Reading the entitlement here is fail-closed by design: when KNIGHT cannot be
    reached and the cache has fallen back to the minimum safe set, a paid
    Feature's menu item is absent rather than shown-but-broken.
    """
    from . import entitlements
    from ..external.contract import external_features

    mounts: list[dict] = []

    # enabled_only filters to installed-and-enabled; the entitlement check is the
    # belt-and-braces that does not wait for KNIGHT's disable job to have landed.
    for contract in external_features(feature_root, enabled_only=True):
        if not entitlements.is_enabled(contract.slug):
            continue

        for mount in contract.ui_mounts:
            mounts.append({**mount, "slug": contract.slug})

    return mounts


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


class ServiceUnavailable(RuntimeError):
    """
    A Feature's service could not be reached, or refused.

    Named on the façade so business code can catch it without importing
    anything deeper. A cron job that has to tell a merchant "the subscriptions
    service is not answering" needs a name for that, and it should not be the
    name of a module it is not allowed to import.
    """


def ask(slug: str, method: str, path: str, payload: dict | None = None) -> dict:
    """
    Asks a Feature's service a question, as the store itself.

    The third direction. `announce` tells a service something happened and
    returns nothing; this asks and waits for an answer, which is what the
    store's own scheduled work needs — "which periods are owed an order" is a
    question only the Feature can answer.

    Nobody is asking on a shopper's behalf, so the store asserts itself rather
    than a person. And it is not on anybody's request path: a slow service
    delays a cron run rather than a checkout.

    Raises :class:`ServiceUnavailable` rather than letting an HTTP exception out,
    so business code never has to know what this is built on
    (`docs/adr/0033-api-driven-features.md`).
    """
    from ..external import ServiceCallFailed, call, contract_for

    contract = contract_for(slug)

    if contract is None:
        raise ServiceUnavailable(f"'{slug}' is not installed on this store as a service.")

    try:
        return call(contract, method, path, payload)
    except ServiceCallFailed as failure:
        raise ServiceUnavailable(str(failure)) from failure


def serves_as_service(slug: str) -> bool:
    """
    Whether this Feature is present as a service rather than as a package.

    The one question business code legitimately has about *how* a Feature
    arrived, because the two shapes are reached differently: a package is
    imported and a service is asked. Everything else about the difference stays
    behind this façade.
    """
    from ..external import contract_for

    return contract_for(slug) is not None


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
    "ServiceUnavailable",
    "announce",
    "ask",
    "known_events",
    "serves_as_service",
    "FeatureNotEntitled",
    "current",
    "installed_features",
    "is_available",
    "is_enabled",
    "is_installed",
    "refresh",
    "require",
    "visible_ui_mounts",
]
