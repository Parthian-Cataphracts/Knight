"""
The entitlement cache and the rules for trusting it.

KNIGHT is the source of truth for what a customer is owed. This store enforces
it, which means it must have an answer even when KNIGHT cannot be reached — and
that answer must not be "everything is allowed".

The rules, from docs/store-integration.md §3:

- The set is refreshed on a schedule and cached with a TTL.
- The payload is signed, and a cached set whose signature does not verify is
  discarded rather than used. This is what makes the cache safe to keep on
  disk-backed caches shared with other processes.
- On prolonged failure to refresh, the last known good set is enforced for a
  bounded grace period, then the minimum safe set — the capabilities every store
  has, and nothing paid for.
- Entitlement is not installation. This module answers "is it paid for"; whether
  the code is present is a separate question the registry answers.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import logging
import time
from dataclasses import dataclass
from typing import Any

from ..conf import get_settings

logger = logging.getLogger(__name__)

_CACHE_KEY = "knight:entitlements"
_LAST_GOOD_CACHE_KEY = "knight:entitlements:last-good"

SIGNATURE_VERSION = "1"

#: Capabilities every store has whatever happens. Falling back to these is the
#: floor: a store that cannot reach KNIGHT keeps serving shoppers, and stops
#: offering anything anyone has to pay for.
MINIMUM_SAFE_FEATURES = ("storefront", "order-management")


@dataclass(frozen=True)
class EntitlementSet:
    slugs: frozenset[str]
    issued_at: float
    stale_after: float
    source: str

    def is_enabled(self, slug: str) -> bool:
        return slug in self.slugs

    @property
    def is_fresh(self) -> bool:
        return time.time() < self.stale_after


def is_enabled(slug: str) -> bool:
    """
    Whether this store may serve a capability.

    The one question business code asks, and the only entry point it should use.
    """
    return current().is_enabled(slug)


def current() -> EntitlementSet:
    """
    The set to enforce right now, from the cache, refreshing if it is stale.

    Never raises: an entitlement question asked while KNIGHT is down still has to
    have an answer, and a storefront that 500s because the control plane is
    unreachable would be a worse outage than the one it is reacting to.
    """
    from django.core.cache import cache

    cached = cache.get(_CACHE_KEY)
    if cached:
        cached_set = _from_cache(cached, source="cache")
        if cached_set and cached_set.is_fresh:
            return cached_set

    try:
        return refresh()
    except Exception as exc:  # noqa: BLE001 - deliberately broad; see the docstring
        logger.warning("Could not refresh entitlements from KNIGHT: %s", exc)
        return _fallback()


def refresh() -> EntitlementSet:
    """
    Pulls the set from KNIGHT, verifies the signature, and caches it.

    A payload whose signature does not verify is refused outright. It means
    either that the key is wrong — a rotation this store has not caught up with —
    or that the payload is not from KNIGHT, and neither is a reason to start
    enforcing it.
    """
    from django.core.cache import cache

    from ..auth import get_session
    from ..client import KnightClient

    session = get_session()
    payload = KnightClient().fetch_entitlements()

    if not verify(payload, session.entitlement_signing_key):
        raise ValueError("The entitlement payload from KNIGHT did not verify against this store's key.")

    slugs = frozenset(feature["slug"] for feature in payload.get("features", []))
    issued_at = _parse_timestamp(payload.get("issuedAt"))
    stale_after = _parse_timestamp(payload.get("staleAfter"))

    entry = {
        "slugs": sorted(slugs),
        "issuedAt": issued_at,
        "staleAfter": stale_after,
    }

    config = get_settings()
    cache.set(_CACHE_KEY, entry, timeout=max(int(stale_after - time.time()), 60))

    # The last known good copy outlives the fresh one on purpose: it is what the
    # store falls back to, so it must still be there once the fresh copy expires.
    cache.set(_LAST_GOOD_CACHE_KEY, entry, timeout=config.entitlement_grace_seconds)

    logger.info("Entitlements refreshed from KNIGHT: %s", ", ".join(sorted(slugs)) or "(none)")

    return EntitlementSet(slugs=slugs, issued_at=issued_at, stale_after=stale_after, source="knight")


def verify(payload: dict[str, Any], signing_key: str) -> bool:
    """
    Checks the HMAC KNIGHT computed over the canonical form of this payload.

    The canonical form is a flat string, not the JSON body: two languages will
    never agree byte-for-byte on JSON, and a signature that only sometimes
    verifies would be worse than none at all. Timestamps in it are Unix seconds
    for the same reason.
    """
    signature = payload.get("signature")
    if not signature or not signing_key:
        return False

    if str(payload.get("signatureVersion", SIGNATURE_VERSION)) != SIGNATURE_VERSION:
        # A newer canonicalisation this store does not know how to reproduce.
        # Refusing is right: verifying it the old way would be a guess.
        logger.warning("KNIGHT signed the entitlement set with an unknown signature version.")
        return False

    expected = base64.b64encode(
        hmac.new(base64.b64decode(signing_key), canonicalise(payload).encode("utf-8"), hashlib.sha256).digest()
    ).decode("ascii")

    return hmac.compare_digest(expected, str(signature))


def canonicalise(payload: dict[str, Any]) -> str:
    """
    The exact string KNIGHT signed. Mirrors EntitlementSignature on the KNIGHT
    side; both are tested against docs/contracts/store-integration.schema.json.
    """
    features = sorted(payload.get("features", []), key=lambda feature: feature["slug"])
    rendered = ",".join(
        f"{feature['slug']}:{_unix(feature.get('expiresAt')) if feature.get('expiresAt') else '-'}"
        for feature in features
    )

    return "|".join(
        [
            "knight-entitlements",
            SIGNATURE_VERSION,
            str(payload.get("storeId", "")),
            str(payload.get("customerId", "")),
            str(payload.get("environment", "")),
            str(_unix(payload.get("issuedAt"))),
            str(_unix(payload.get("staleAfter"))),
            rendered,
        ]
    )


def _fallback() -> EntitlementSet:
    """
    What to enforce when KNIGHT cannot be reached: the last known good set while
    it is inside the grace window, and the minimum safe set after that.
    """
    from django.core.cache import cache

    last_good = cache.get(_LAST_GOOD_CACHE_KEY)
    if last_good:
        config = get_settings()
        age = time.time() - float(last_good.get("issuedAt", 0))

        if age <= config.entitlement_grace_seconds:
            logger.warning(
                "Enforcing the last known good entitlement set, %.0f seconds old, while KNIGHT is unreachable.",
                age,
            )
            return _from_cache(last_good, source="last-known-good") or _minimum()

        logger.error(
            "The last known good entitlement set is %.0f seconds old, past the %s second grace period. "
            "Falling back to the minimum safe set.",
            age,
            config.entitlement_grace_seconds,
        )

    return _minimum()


def _minimum() -> EntitlementSet:
    now = time.time()
    return EntitlementSet(
        slugs=frozenset(MINIMUM_SAFE_FEATURES),
        issued_at=now,
        stale_after=now,
        source="minimum-safe",
    )


def _from_cache(entry: dict[str, Any], source: str) -> EntitlementSet | None:
    try:
        return EntitlementSet(
            slugs=frozenset(entry["slugs"]),
            issued_at=float(entry["issuedAt"]),
            stale_after=float(entry["staleAfter"]),
            source=source,
        )
    except (KeyError, TypeError, ValueError):
        logger.warning("Discarded an unreadable cached entitlement set.")
        return None


def _parse_timestamp(value: Any) -> float:
    from datetime import datetime

    if value is None:
        return time.time()

    if isinstance(value, (int, float)):
        return float(value)

    text = str(value).replace("Z", "+00:00")
    try:
        return datetime.fromisoformat(text).timestamp()
    except ValueError:
        return time.time()


def _unix(value: Any) -> int:
    return int(_parse_timestamp(value))
