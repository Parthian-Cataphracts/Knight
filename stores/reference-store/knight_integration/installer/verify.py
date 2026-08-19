"""
Deciding whether a delivered artifact may be installed.

This is the most security-critical module in the store. Everything downstream —
unpacking, migrating, enabling — assumes the bytes are what KNIGHT published,
and this is the only thing that establishes it.

Two independent checks, in this order and never fewer:

1. **Digest.** The bytes on disk hash to what KNIGHT said they would. This
   catches truncation, corruption and the wrong file.
2. **Signature.** A key this store already trusts signed that digest. This
   catches an attacker who can substitute the artifact *and* the digest —
   which is exactly the position anyone who compromises the delivery channel or
   the object store is in.

The signature is verified against a public key from the store's own
configuration, never against one supplied with the job. A key that travels with
the payload it vouches for proves nothing at all
(docs/adr/0015-feature-delivery-mechanism.md).
"""

from __future__ import annotations

import base64
import hashlib
import logging
from pathlib import Path

logger = logging.getLogger(__name__)

#: Read in chunks: a feature artifact can be tens of megabytes and a store that
#: loads one into memory to hash it is a store that falls over on a small box.
_CHUNK_SIZE = 1024 * 1024


class ArtifactRejected(RuntimeError):
    """
    The artifact is not what it claims to be, and must not be installed.

    Deliberately not a subclass of the client's error types: this is never a
    transport problem and retrying never helps. It means somebody or something
    handed this store code it did not expect.
    """

    def __init__(self, code: str, detail: str) -> None:
        super().__init__(detail)
        self.code = code
        self.detail = detail


def compute_digest(path: Path) -> str:
    """The sha-256 of a file, lowercase hex — the same spelling KNIGHT stores."""
    digest = hashlib.sha256()

    with path.open("rb") as handle:
        while chunk := handle.read(_CHUNK_SIZE):
            digest.update(chunk)

    return digest.hexdigest()


def verify_digest(path: Path, expected_digest: str) -> str:
    """
    Confirms the downloaded bytes hash to what KNIGHT published.

    Compared with :func:`hmac.compare_digest` rather than ``==``. A digest is not
    a secret, so this is not about timing attacks — it is about never growing a
    habit of comparing security-relevant values the sloppy way.
    """
    import hmac

    actual = compute_digest(path)
    expected = (expected_digest or "").strip().lower()

    if not expected:
        raise ArtifactRejected("digest.missing", "The job did not say what the artifact should hash to.")

    if not hmac.compare_digest(actual, expected):
        raise ArtifactRejected(
            "digest.mismatch",
            f"The downloaded artifact hashes to {actual}, but KNIGHT published {expected}.",
        )

    return actual


def verify_signature(digest: str, signature: str, key_id: str, trusted_keys: dict[str, str]) -> None:
    """
    Confirms a key this store already trusts signed the digest.

    ``trusted_keys`` maps key id to a base64 DER SubjectPublicKeyInfo, and comes
    from this store's configuration. An unknown key id is a refusal, never a
    pass: an artifact signed by something this store cannot identify is precisely
    what the check exists to stop.
    """
    if not signature:
        raise ArtifactRejected("signature.missing", "The artifact is not signed.")

    public_key_der = (trusted_keys or {}).get(key_id)
    if not public_key_der:
        raise ArtifactRejected(
            "signature.unknown_key",
            f"The artifact is signed by key '{key_id}', which this store does not trust.",
        )

    try:
        from cryptography.exceptions import InvalidSignature
        from cryptography.hazmat.primitives import hashes, serialization
        from cryptography.hazmat.primitives.asymmetric import ec
    except ImportError as exc:  # pragma: no cover - a deployment problem, not a code path
        # Refusing is the only safe answer. Installing unverified code because a
        # library is missing would turn a packaging mistake into a supply-chain
        # incident.
        raise ArtifactRejected(
            "signature.unavailable",
            "The 'cryptography' package is not installed, so artifact signatures cannot be verified.",
        ) from exc

    try:
        public_key = serialization.load_der_public_key(base64.b64decode(public_key_der))
    except (ValueError, TypeError) as exc:
        raise ArtifactRejected(
            "signature.bad_key",
            f"The configured public key for '{key_id}' could not be read.",
        ) from exc

    if not isinstance(public_key, ec.EllipticCurvePublicKey):
        raise ArtifactRejected(
            "signature.bad_key",
            f"The configured key '{key_id}' is not an elliptic-curve public key.",
        )

    try:
        # KNIGHT signs the digest string, not the artifact bytes: the store has
        # already hashed the file once, and hashing tens of megabytes a second
        # time to verify a short signature would be pure waste.
        public_key.verify(
            base64.b64decode(signature),
            digest.strip().lower().encode("ascii"),
            ec.ECDSA(hashes.SHA256()),
        )
    except InvalidSignature as exc:
        raise ArtifactRejected(
            "signature.invalid",
            f"The signature over the artifact digest is not valid for key '{key_id}'.",
        ) from exc
    except (ValueError, TypeError) as exc:
        raise ArtifactRejected("signature.malformed", "The signature is not readable.") from exc


def verify_artifact(path: Path, expected_digest: str, signature: str, key_id: str, trusted_keys: dict[str, str]) -> str:
    """
    Both checks, in the only order that means anything.

    The signature is over the digest, so verifying it before confirming the
    digest actually matches the file would prove that KNIGHT signed *a* digest
    while saying nothing about the bytes on disk.
    """
    digest = verify_digest(path, expected_digest)
    verify_signature(digest, signature, key_id, trusted_keys)

    logger.info("Artifact at %s verified against digest %s and key %s.", path.name, digest[:12], key_id)
    return digest
