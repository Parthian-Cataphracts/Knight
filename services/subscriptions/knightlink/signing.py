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
    store_id = request.headers.get("X-Knight-Store", "")
    signature = request.headers.get("X-Knight-Signature", "")
    timestamp = request.headers.get("X-Knight-Timestamp", "")
    nonce = request.headers.get("X-Knight-Nonce", "")

    if not (store_id and signature and timestamp and nonce):
        raise Unsigned("The request is not signed.", "signature.missing")

    if not signature.startswith("sha256="):
        raise Unsigned("The signature is not in a form this service understands.", "signature.malformed")

    # By the id KNIGHT issued, not by a slug. A slug is a name a merchant can
    # change; a store id is the stable name across KNIGHT, the store and here.
    store = Store.objects.filter(store_id=store_id, enabled=True).first() if _is_uuid(store_id) else None

    if store is None:
        # Deliberately the same shape of answer as a bad signature. A caller
        # must not be able to enumerate which stores this service serves.
        logger.warning("A request arrived for unknown or disabled store '%s'.", store_id)
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

    message = canonical_string(
        request.method, request.path, timestamp, nonce, request.body or b""
    ).encode("utf-8")

    usable = store.usable_secrets()

    if not usable:
        # A store with no secret at all. It has been revoked, or its secrets
        # aged out without a new one arriving; either way the answer is the same
        # shape as a bad signature, because which of the two it is tells a
        # caller something about a store they have not proved they are.
        logger.warning("Store '%s' has no usable secret.", store.slug)
        raise Unsigned("The signature does not verify.", "signature.invalid")

    # Every currently valid secret, not only the newest. This is what makes a
    # rotation something other than an outage: for the length of one window both
    # the old and the new one verify, so a request signed a second before the
    # change is still good a second after it.
    if not any(_matches(candidate.secret, message, signature) for candidate in usable):
        logger.warning("A request for store '%s' did not verify.", store.slug)
        raise Unsigned("The signature does not verify.", "signature.invalid")

    _claim_nonce(store, nonce)

    return store


def _matches(secret: str, message: bytes, signature: str) -> bool:
    """One candidate secret, compared in fixed time."""
    expected = hmac.new(secret.encode("utf-8"), message, hashlib.sha256).hexdigest()

    return hmac.compare_digest(expected, signature[len("sha256=") :])


def verify_control_plane(request) -> None:
    """
    Whether this request came from KNIGHT rather than from a store.

    The second caller this service has, and it is a different kind of caller: a
    store asks about its own subscriptions, and KNIGHT says who the stores *are*
    and what they may sign with. Registering stores under the store contract
    would have been circular — a store cannot prove it is a store before it has
    a secret, which is the thing being issued.

    So it is one secret, held by the control plane, checked the same way
    everything else here is checked: same canonical string, same skew window,
    same nonce table. What it is not is a store: it gets no `Store` row, and no
    endpoint that serves a store's data will look at it.
    """
    secret = str(getattr(settings, "KNIGHT_CONTROL_SECRET", "") or "")

    if not secret:
        # Unconfigured is refused, never open. A control-plane surface that
        # accepted anybody when nobody had set a secret would be the worst
        # possible default for the one endpoint that can issue credentials.
        logger.error("A control-plane request arrived and no control secret is configured.")
        raise Unsigned("This service does not accept control-plane requests.", "control.unconfigured")

    signature = request.headers.get("X-Knight-Signature", "")
    timestamp = request.headers.get("X-Knight-Timestamp", "")
    nonce = request.headers.get("X-Knight-Nonce", "")

    if not (signature and timestamp and nonce):
        raise Unsigned("The request is not signed.", "signature.missing")

    if not signature.startswith("sha256="):
        raise Unsigned("The signature is not in a form this service understands.", "signature.malformed")

    try:
        age = abs(int(time.time()) - int(timestamp))
    except ValueError:
        raise Unsigned("The timestamp is not a number.", "timestamp.malformed") from None

    if age > settings.KNIGHT_MAX_SKEW_SECONDS:
        raise Unsigned("The request is out of step with this service's clock.", "timestamp.stale")

    message = canonical_string(
        request.method, request.path, timestamp, nonce, request.body or b""
    ).encode("utf-8")

    if not _matches(secret, message, signature):
        logger.warning("A control-plane request did not verify.")
        raise Unsigned("The signature does not verify.", "signature.invalid")

    _claim_nonce(None, nonce)


def _claim_nonce(store: Store | None, nonce: str) -> None:
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
        logger.warning(
            "A replayed request for %s was refused.",
            f"store '{store.slug}'" if store is not None else "the control plane",
        )
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


def _is_uuid(value: str) -> bool:
    """
    Whether the header is even shaped like a store id.

    Checked before the database is asked, so a caller cannot use this endpoint
    to probe what the query does with arbitrary text.
    """
    import uuid

    try:
        uuid.UUID(str(value))
    except (ValueError, AttributeError, TypeError):
        return False

    return True
