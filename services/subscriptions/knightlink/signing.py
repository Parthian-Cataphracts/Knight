"""
Verifying that a request really came from the store it claims to be.

The other end of ``knight_integration/external/signing.py`` in the reference
store. Both sides build the same canonical string independently and neither
sends it — a signature over a string one party supplied proves only that the
party agrees with itself.

    METHOD \\n path \\n timestamp \\n nonce \\n sha256(body)

Four checks, in this order, and the order is the point:

1. **The store is known and enabled.** An unknown store id is refused before any
   cryptography happens, so a stranger cannot use this endpoint as an oracle for
   which stores exist by timing it — the answer is the same shape either way.
2. **The timestamp is inside the window.** Cheap, and it bounds everything below.
3. **The HMAC matches**, in fixed time.
4. **The nonce has not been used.** Last, because it is the only check that
   writes, and a request that fails any of the three above must not leave a row
   behind — otherwise an attacker could burn a legitimate store's nonce space
   with unsigned requests.
"""

from __future__ import annotations

import hashlib
import hmac
import logging
import time

from django.conf import settings
from django.db import IntegrityError, transaction
from django.utils import timezone

from .models import SeenNonce, Store

logger = logging.getLogger(__name__)


class Unsigned(Exception):
    """The request did not carry a usable signature. Refused as 401."""

    def __init__(self, reason: str, code: str) -> None:
        super().__init__(reason)
        self.reason = reason
        self.code = code


def canonical_string(method: str, path: str, timestamp: str, nonce: str, body: bytes) -> str:
    """
    The exact bytes both ends sign.

    Newline-separated, never re-ordered, and the body is covered by its digest
    rather than inlined — a subscription cancellation is exactly the request
    somebody in the middle would want to alter.
    """
    digest = hashlib.sha256(body or b"").hexdigest()

    return "\n".join([method.upper(), path, timestamp, nonce, digest])


def verify(request) -> Store:
    """
    Returns the store that signed this request, or raises :class:`Unsigned`.

    Every refusal carries a code rather than a message alone, because the store
    integrating against this needs to tell "my clock is wrong" from "my secret is
    wrong", and those look identical from a 401.
    """
    store_slug = request.headers.get("X-Knight-Store", "")
    signature = request.headers.get("X-Knight-Signature", "")
    timestamp = request.headers.get("X-Knight-Timestamp", "")
    nonce = request.headers.get("X-Knight-Nonce", "")

    if not (store_slug and signature and timestamp and nonce):
        raise Unsigned("The request is not signed.", "signature.missing")

    if not signature.startswith("sha256="):
        raise Unsigned("The signature is not in a form this service understands.", "signature.malformed")

    store = Store.objects.filter(slug=store_slug, enabled=True).first()

    if store is None:
        # Deliberately the same shape of answer as a bad signature. A caller
        # must not be able to enumerate which stores this service serves.
        logger.warning("A request arrived for unknown or disabled store '%s'.", store_slug)
        raise Unsigned("This service does not answer that store.", "store.unknown")

    try:
        age = abs(int(time.time()) - int(timestamp))
    except ValueError:
        raise Unsigned("The timestamp is not a number.", "timestamp.malformed") from None

    if age > settings.KNIGHT_MAX_SKEW_SECONDS:
        raise Unsigned(
            f"The request is {age}s out of step and this service accepts "
            f"{settings.KNIGHT_MAX_SKEW_SECONDS}s.",
            "timestamp.stale",
        )

    expected = hmac.new(
        store.secret.encode("utf-8"),
        canonical_string(request.method, request.path, timestamp, nonce, request.body or b"").encode("utf-8"),
        hashlib.sha256,
    ).hexdigest()

    if not hmac.compare_digest(expected, signature[len("sha256=") :]):
        logger.warning("A request for store '%s' did not verify.", store_slug)
        raise Unsigned("The signature does not verify.", "signature.invalid")

    _claim_nonce(store, nonce)

    return store


def _claim_nonce(store: Store, nonce: str) -> None:
    """
    Records the nonce, and refuses if it was already there.

    The insert **is** the check. Asking first and inserting second leaves a
    window in which two replays of one captured request both find nothing, and
    the whole defence is against something happening twice.
    """
    try:
        with transaction.atomic():
            SeenNonce.objects.create(store=store, nonce=nonce)
    except IntegrityError:
        logger.warning("A replayed request for store '%s' was refused.", store.slug)
        raise Unsigned("This request has already been received.", "nonce.replayed") from None


def forget_old_nonces(now=None) -> int:
    """
    Drops nonces older than the window that makes them matter.

    Kept for at least twice the skew window: a nonce forgotten while its
    timestamp is still acceptable would leave a replay hole exactly as wide as
    the difference. Run on a timer; the table is otherwise unbounded.
    """
    from datetime import timedelta

    cutoff = (now or timezone.now()) - timedelta(seconds=settings.KNIGHT_NONCE_TTL_SECONDS)
    deleted, _ = SeenNonce.objects.filter(seen_at__lt=cutoff).delete()

    return deleted
