"""
Verifying that a request really came from KNIGHT.

The health payload names this store's version, its dependencies and the
features it has installed. That is a useful page for an operator and an equally
useful one for somebody deciding what to attack, so the endpoint is
authenticated rather than public (docs/store-integration.md §5).

The proof is an HMAC over a small canonical string, computed with the same
per-store key this store received in its handshake. There is no new secret to
distribute, and a store that has never handshaken has nothing to verify with —
which is why the domain-verification endpoint, the one that runs before any
handshake, is deliberately not protected this way.
"""

from __future__ import annotations

import base64
import hashlib
import hmac
import logging
import time

from django.http import HttpRequest

from ..auth import get_session
from ..conf import get_settings

logger = logging.getLogger(__name__)

SIGNATURE_VERSION = "1"

STORE_HEADER = "X-Knight-Store"
TIMESTAMP_HEADER = "X-Knight-Timestamp"
NONCE_HEADER = "X-Knight-Nonce"
SIGNATURE_HEADER = "X-Knight-Signature"
VERSION_HEADER = "X-Knight-Signature-Version"


def canonicalise(method: str, path: str, timestamp: str, nonce: str) -> str:
    """
    The exact string both sides hash. Mirrors StoreRequestSignature on the
    KNIGHT side.

    Only the path is signed, never the host: a proxy in front of the store may
    legitimately rewrite the host, and binding the signature to it would break
    every store behind one.
    """
    return f"knight-request|{SIGNATURE_VERSION}|{method.upper()}|{path}|{timestamp}|{nonce}"


def is_signed_by_knight(request: HttpRequest) -> bool:
    """
    Whether this request carries a valid KNIGHT signature.

    Returns False rather than raising for every failure mode, including a store
    that cannot currently reach KNIGHT to obtain its key: an unverifiable request
    is simply not authenticated.
    """
    signature = request.headers.get(SIGNATURE_HEADER)
    timestamp = request.headers.get(TIMESTAMP_HEADER)
    nonce = request.headers.get(NONCE_HEADER, "")

    if not signature or not timestamp:
        return False

    if request.headers.get(VERSION_HEADER, SIGNATURE_VERSION) != SIGNATURE_VERSION:
        return False

    config = get_settings()

    # A signature is only good for a few minutes, so a captured request cannot be
    # replayed tomorrow. The window is generous enough for ordinary clock drift
    # between two machines nobody synchronises.
    try:
        skew = abs(time.time() - int(timestamp))
    except ValueError:
        return False

    if skew > config.request_signature_skew_seconds:
        logger.warning("Refused a KNIGHT request whose timestamp was %.0f seconds off.", skew)
        return False

    try:
        key = get_session().entitlement_signing_key
    except Exception as exc:  # noqa: BLE001 - an unreachable KNIGHT is not an authenticated caller
        logger.warning("Could not obtain this store's signing key to verify a request: %s", exc)
        return False

    if not key:
        return False

    expected = base64.b64encode(
        hmac.new(
            base64.b64decode(key),
            canonicalise(request.method or "GET", request.path, str(timestamp), nonce).encode("utf-8"),
            hashlib.sha256,
        ).digest()
    ).decode("ascii")

    return hmac.compare_digest(expected, signature)
