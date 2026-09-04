"""
Credential handling and the store token cache.

The store holds a long-lived client id and secret and exchanges them for a
short-lived token, which is what every other call carries
(docs/authentication.md §2). The exchange happens on demand: the first call
that needs a token performs the handshake, and everything after it reuses the
cached one until shortly before it expires.

The token, and the key KNIGHT signs this store's payloads with, live in the
Django cache. They are re-obtainable at any time from the credential, so losing
them costs one round trip and nothing else.
"""

from __future__ import annotations

import logging
import threading
import time
from dataclasses import dataclass
from typing import Any

from .conf import KnightSettings, get_settings

logger = logging.getLogger(__name__)

_TOKEN_CACHE_KEY = "knight:session"

# Renew this long before the token actually expires, so a request never fails
# because the token died between the check and the call.
_RENEW_MARGIN_SECONDS = 60

_lock = threading.Lock()


class KnightAuthError(RuntimeError):
    """Raised when KNIGHT refuses this store's credentials."""


@dataclass(frozen=True)
class StoreSession:
    access_token: str
    expires_at: float
    store_id: str
    environment: str
    entitlement_signing_key: str
    integration_status: str
    domain_verification_outstanding: bool
    domain_verification_token: str
    heartbeat_seconds: int
    feature_refresh_seconds: int

    @property
    def is_usable(self) -> bool:
        return bool(self.access_token) and time.time() < self.expires_at - _RENEW_MARGIN_SECONDS

    def to_cache(self) -> dict[str, Any]:
        return dict(self.__dict__)

    @classmethod
    def from_cache(cls, payload: dict[str, Any]) -> "StoreSession":
        return cls(**payload)


def get_session(force_refresh: bool = False) -> StoreSession:
    """
    Returns a usable session, performing a handshake if there is not one.

    Serialised behind a lock so a burst of requests on a cold cache produces one
    handshake rather than one per worker thread.
    """
    from django.core.cache import cache

    if not force_refresh:
        cached = cache.get(_TOKEN_CACHE_KEY)
        if cached:
            session = StoreSession.from_cache(cached)
            if session.is_usable:
                return session

    with _lock:
        # Re-checked inside the lock: another thread may have handshaken while
        # this one waited.
        cached = cache.get(_TOKEN_CACHE_KEY)
        if cached and not force_refresh:
            session = StoreSession.from_cache(cached)
            if session.is_usable:
                return session

        session = _handshake(get_settings())
        cache.set(_TOKEN_CACHE_KEY, session.to_cache(), timeout=max(int(session.expires_at - time.time()), 30))
        return session


def forget_session() -> None:
    """Drops the cached token, so the next call handshakes again."""
    from django.core.cache import cache

    cache.delete(_TOKEN_CACHE_KEY)


def _handshake(config: KnightSettings) -> StoreSession:
    # Imported here rather than at module scope: client imports this module for
    # the session, and importing it back at the top would be a cycle.
    from .client import KnightClient

    config.require_credentials()

    body = KnightClient(config).handshake()

    # If KNIGHT rotated a credential nearing expiry, it handed the replacement
    # back on this response. Adopt it now; the token just minted stays valid
    # through its grace window and the next handshake uses the stored one.
    from .credentials import adopt_if_rotated

    adopt_if_rotated(config, body)

    session = StoreSession(
        access_token=body["accessToken"],
        expires_at=time.time() + int(body.get("expiresIn", 0)),
        store_id=body.get("storeId", config.store_id),
        environment=body.get("environment", config.environment),
        entitlement_signing_key=body.get("entitlementSigningKey", ""),
        integration_status=body.get("integrationStatus", "Pending"),
        domain_verification_outstanding=bool(body.get("domainVerificationOutstanding", False)),
        domain_verification_token=body.get("domainVerificationToken") or "",
        heartbeat_seconds=int(body.get("heartbeatSeconds", 60)),
        feature_refresh_seconds=int(body.get("featureRefreshSeconds", config.feature_refresh_seconds)),
    )

    logger.info(
        "Handshake accepted by KNIGHT: store %s, status %s%s",
        session.store_id,
        session.integration_status,
        ", domain verification outstanding" if session.domain_verification_outstanding else "",
    )

    return session
