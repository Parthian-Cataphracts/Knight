"""
Reading this Feature's own configuration.

The same contract as every other Feature that needs configuration: KNIGHT's
install pipeline writes `knight_config.json` beside the delivered package, in the
shape `{version, values, secrets}` (`docs/feature-delivery.md` §9).

**There are no secrets here, and that is the interesting part.** This is the
Feature with the most third-party surface in the catalogue and it takes no
credential from KNIGHT, because the credentials it uses are per-connection OAuth
tokens: one per marketplace account, refreshed while the store runs. A static
configuration channel cannot express that, so they live on the connection row and
the store's own database is their trust boundary
(see `models.Connection`).

Every value is clamped. A retry ceiling of zero abandons everything on its first
attempt, and a backoff of zero turns a partner's brief outage into a denial of
service that the merchant's own account gets blocked for.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

SLUG = "external-marketplaces"
CONFIG_FILENAME = "knight_config.json"

DEFAULTS: dict[str, Any] = {
    # How many times an outbound message is tried before it is abandoned and
    # waits for a person. Five: enough to ride out a partner's deploy, few enough
    # that a genuinely broken message is in front of somebody the same day.
    "max_attempts": 5,
    # The gap before each retry, in seconds. Widening steeply, because the
    # failure this is built for is a partner being down for a while rather than a
    # packet being lost: 1 minute, 5, 30, 2 hours, 6 hours.
    "backoff_seconds": [60, 300, 1800, 7200, 21600],
    # How many messages one flush handles. A ceiling rather than a target: a
    # worker that tried to drain a hundred thousand messages in one run would
    # hold a transaction open long enough to matter.
    "flush_limit": 200,
}


def _from_file() -> dict[str, Any]:
    candidate = Path(__file__).resolve().parent.parent / CONFIG_FILENAME

    if not candidate.exists():
        return {}

    try:
        return json.loads(candidate.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # Loud, then carry on with defaults - which retry conservatively and
        # abandon early, the safe end for a Feature that talks to somebody else's
        # rate limiter.
        logger.exception("The configuration at %s could not be read; using defaults.", candidate)
        return {}


def _from_settings() -> dict[str, Any]:
    try:
        from django.conf import settings

        return (getattr(settings, "KNIGHT_FEATURE_CONFIG", {}) or {}).get(SLUG, {}) or {}
    except Exception:  # noqa: BLE001 - settings may not be configured at all
        return {}


def _document() -> dict[str, Any]:
    document = _from_file()

    return document if document else _from_settings()


def values() -> dict[str, Any]:
    return {**DEFAULTS, **(_document().get("values") or {})}


def value(key: str, default: Any = None) -> Any:
    return values().get(key, DEFAULTS.get(key, default))


def max_attempts() -> int:
    try:
        attempts = int(value("max_attempts"))
    except (TypeError, ValueError):
        attempts = int(DEFAULTS["max_attempts"])

    return max(1, min(attempts, 20))


def backoff_seconds(attempt: int) -> int:
    """
    How long to wait before the given attempt number.

    The last configured gap is reused past the end of the list, so a larger
    `max_attempts` than `backoff_seconds` widens rather than crashes. Never
    zero: retrying instantly is what turns a partner's outage into a denial of
    service the merchant's account is blocked for.
    """
    configured = value("backoff_seconds")

    if not isinstance(configured, list) or not configured:
        configured = DEFAULTS["backoff_seconds"]

    index = min(max(1, attempt), len(configured)) - 1

    try:
        seconds = int(configured[index])
    except (TypeError, ValueError):
        seconds = 60

    return max(10, min(seconds, 24 * 60 * 60))


def flush_limit() -> int:
    try:
        limit = int(value("flush_limit"))
    except (TypeError, ValueError):
        limit = int(DEFAULTS["flush_limit"])

    return max(1, min(limit, 5000))


def describe() -> dict[str, Any]:
    """What an operator needs to see. No secrets to hide, and that is stated."""
    document = _document()

    return {"version": document.get("version", 0), "values": values(), "secretsPresent": []}
