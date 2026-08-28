"""
Proving to a Feature's service that a request came from this store.

HMAC-SHA256 over a canonical string, under a secret the two ends share. The
canonical string is built the same way KNIGHT builds its own — method, path,
timestamp, nonce, body digest — because a store integrating two of these should
have to learn the idea once (``docs/store-integration.md`` §4).

Three properties, and each of them is the reason for one line below:

- **the body is covered**, so a proxy in the middle cannot change an order total
  without breaking the signature;
- **the timestamp is covered**, so a captured request stops working;
- **the nonce is covered**, so a captured request cannot be replayed inside the
  window the timestamp still allows.
"""

from __future__ import annotations

import hashlib
import hmac
import logging
import os
import time
import uuid

logger = logging.getLogger(__name__)

#: How far out of step a clock may be before the service should refuse. Sent so
#: the service does not have to guess, and short: the whole point of a timestamp
#: is that it expires.
SKEW_SECONDS = 300


def canonical_string(method: str, path: str, timestamp: str, nonce: str, body: bytes) -> str:
    """
    The exact bytes both ends sign.

    Newline-separated and never re-ordered. A canonical form both sides derive
    independently is the only kind that works; one side sending the string it
    signed would be asking the other to agree with itself.
    """
    digest = hashlib.sha256(body or b"").hexdigest()

    return "\n".join([method.upper(), path, timestamp, nonce, digest])


def sign(secret: str, method: str, path: str, body: bytes = b"") -> dict[str, str]:
    """
    The headers to attach to an outbound request.

    Returns the headers rather than mutating a request object, so the same
    function serves the proxy, the webhook sender and anything written later.
    """
    from ..conf import get_settings

    timestamp = str(int(time.time()))
    nonce = uuid.uuid4().hex
    message = canonical_string(method, path, timestamp, nonce, body)

    signature = hmac.new(secret.encode("utf-8"), message.encode("utf-8"), hashlib.sha256).hexdigest()

    return {
        # Which store this is, by the id KNIGHT issued rather than by a slug a
        # merchant can rename. It is set here rather than by each caller so that
        # a new caller cannot forget it — and one did: both the proxy and the
        # delivery worker were sending the *Feature's* slug, which is a name the
        # service has no reason to know a store by.
        "X-Knight-Store": get_settings().store_id,
        "X-Knight-Timestamp": timestamp,
        "X-Knight-Nonce": nonce,
        "X-Knight-Signature": f"sha256={signature}",
        "X-Knight-Skew-Seconds": str(SKEW_SECONDS),
    }


def verify(secret: str, headers: dict[str, str], method: str, path: str, body: bytes) -> bool:
    """
    Whether an inbound callback really came from the service.

    The store needs this as much as the service does: a Feature's service
    calling back into the store is a request the store must not take on trust
    just because it names a Feature it has installed.
    """
    supplied = str(headers.get("X-Knight-Signature") or "")
    timestamp = str(headers.get("X-Knight-Timestamp") or "")
    nonce = str(headers.get("X-Knight-Nonce") or "")

    if not supplied.startswith("sha256=") or not timestamp or not nonce:
        return False

    try:
        age = abs(int(time.time()) - int(timestamp))
    except ValueError:
        return False

    if age > SKEW_SECONDS:
        return False

    message = canonical_string(method, path, timestamp, nonce, body)
    expected = hmac.new(secret.encode("utf-8"), message.encode("utf-8"), hashlib.sha256).hexdigest()

    # Fixed-time, because this one really is a secret comparison.
    return hmac.compare_digest(expected, supplied[len("sha256=") :])


def secret_for(contract, *, required: bool = True) -> str:
    """
    The shared secret for one Feature: what KNIGHT delivered, or the environment.

    Never from the manifest. The manifest is public, signed and kept in a
    catalogue, so a secret in one would be a secret in every copy of it for
    ever; it names the variable and nothing more.

    **The delivered configuration wins.** KNIGHT issues this secret per (store,
    feature) and rotates it, and a rotation reaches the store as a new
    configuration version written beside the registry
    (`docs/adr/0034-a-shared-secret-has-a-lifetime.md`). An environment variable
    that took precedence would mean a store pinned to whatever an operator typed
    on the first day, quietly ignoring every rotation after it — which looks
    exactly like a rotation that worked, until the overlap window closes.

    The environment is still read, and it is not a fallback for a store that has
    simply not been configured: it is what a developer runs against a service on
    their own laptop, and what an operator has while a store is being brought up.
    """
    value = _delivered_secret(contract) or os.environ.get(contract.secret_name, "")

    if not value and required:
        raise LookupError(
            f"{contract.slug} needs the shared secret in {contract.secret_name}, "
            "and neither KNIGHT nor the environment has provided one."
        )

    return value


def _delivered_secret(contract) -> str:
    """
    The secret from the configuration KNIGHT delivered for this Feature, if any.

    Read from the file every time rather than cached. A rotation is written by
    the installer while this process is running, and a value cached at import
    would keep a store signing with a secret whose window is closing — the one
    failure this whole arrangement exists to avoid.

    Never raises. A missing or unreadable configuration is a store that has not
    been given a secret yet, which the caller reports; a traceback here would
    turn "not configured" into "this store is broken".
    """
    import json
    from pathlib import Path

    try:
        from ..conf import get_settings

        path = Path(get_settings().feature_root) / f"{contract.slug}.config.json"

        if not path.is_file():
            return ""

        document = json.loads(path.read_text(encoding="utf-8"))
        secrets = document.get("secrets") or {}

        return str(secrets.get(contract.secret_name) or "")
    except (OSError, ValueError, AttributeError):
        logger.warning(
            "The delivered configuration for '%s' could not be read; "
            "falling back to the environment.",
            contract.slug,
        )
        return ""
