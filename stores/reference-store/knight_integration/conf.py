"""
Configuration for the KNIGHT integration layer.

Everything the layer needs is read once, here, and validated on the way. A
missing client id is a startup problem, not a mystery 401 at three in the
morning, and a store pointed at the wrong environment should say so before it
ships a single error event.

Nothing in this module imports a business model, and nothing in the business
apps imports this module (docs/store-integration.md §1).
"""

from __future__ import annotations

from dataclasses import dataclass

from django.conf import settings


class KnightConfigurationError(RuntimeError):
    """Raised when the store is not configured well enough to talk to KNIGHT."""


VALID_ENVIRONMENTS = ("Development", "Staging", "Production")


@dataclass(frozen=True)
class KnightSettings:
    base_url: str
    client_id: str
    client_secret: str
    environment: str
    store_id: str
    store_version: str
    error_reporting: bool
    log_shipping: bool
    feature_refresh_seconds: int
    timeout_seconds: int
    entitlement_grace_seconds: int
    error_batch_size: int
    error_queue_limit: int
    error_flush_seconds: int
    domain_verification_token: str
    request_signature_skew_seconds: int

    @property
    def is_registered(self) -> bool:
        """Whether this store has credentials at all. False on a fresh checkout."""
        return bool(self.client_id and self.client_secret)

    def require_credentials(self) -> None:
        if not self.is_registered:
            raise KnightConfigurationError(
                "KNIGHT_CLIENT_ID and KNIGHT_CLIENT_SECRET must be set. Register the store in the "
                "KNIGHT dashboard, issue a credential, and put the secret in this store's environment "
                "— never in the repository."
            )

        if self.environment not in VALID_ENVIRONMENTS:
            raise KnightConfigurationError(
                f"KNIGHT_ENVIRONMENT must be one of {', '.join(VALID_ENVIRONMENTS)}, not '{self.environment}'."
            )

        if not self.base_url.startswith(("http://", "https://")):
            raise KnightConfigurationError("KNIGHT_BASE_URL must be an absolute http(s) URL.")

        # A production store talking plain HTTP to its control plane would send
        # its client secret in the clear on every handshake.
        if self.environment == "Production" and not self.base_url.startswith("https://"):
            raise KnightConfigurationError("A Production store must reach KNIGHT over HTTPS.")


def get_settings() -> KnightSettings:
    raw = getattr(settings, "KNIGHT", {})

    return KnightSettings(
        base_url=str(raw.get("BASE_URL", "")).rstrip("/"),
        client_id=str(raw.get("CLIENT_ID", "")),
        client_secret=str(raw.get("CLIENT_SECRET", "")),
        environment=str(raw.get("ENVIRONMENT", "Development")),
        store_id=str(raw.get("STORE_ID", "")),
        store_version=str(raw.get("STORE_VERSION", "0.0.0")),
        error_reporting=bool(raw.get("ERROR_REPORTING", True)),
        log_shipping=bool(raw.get("LOG_SHIPPING", False)),
        feature_refresh_seconds=int(raw.get("FEATURE_REFRESH_SECONDS", 300)),
        timeout_seconds=int(raw.get("TIMEOUT_SECONDS", 5)),
        entitlement_grace_seconds=int(raw.get("ENTITLEMENT_GRACE_SECONDS", 86400)),
        error_batch_size=int(raw.get("ERROR_BATCH_SIZE", 20)),
        error_queue_limit=int(raw.get("ERROR_QUEUE_LIMIT", 500)),
        error_flush_seconds=int(raw.get("ERROR_FLUSH_SECONDS", 10)),
        domain_verification_token=str(raw.get("DOMAIN_VERIFICATION_TOKEN", "")),
        request_signature_skew_seconds=int(raw.get("REQUEST_SIGNATURE_SKEW_SECONDS", 300)),
    )
