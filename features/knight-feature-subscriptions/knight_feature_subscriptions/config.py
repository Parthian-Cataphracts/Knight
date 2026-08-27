"""
Reading this Feature's own configuration.

The same contract as every other Feature that needs configuration: KNIGHT's
install pipeline writes `knight_config.json` beside the delivered package, in the
shape `{version, values, secrets}` (`docs/feature-delivery.md` §9).

This one **has a secret**, and it is the most dangerous one in the catalogue: a
key that can take money from a shopper's payment method. Everything about how it
is handled is the arrangement `marketing-automation` and `ai-reports` established
in phase 15 — delivered over the install channel, never in the package, never
returned by the configuration endpoint, never logged, and never in an error
message.

Every numeric value is clamped rather than trusted. A subscriptions Feature
misconfigured at eleven at night is one that charges people, and each of these
has a setting that would do damage: retries with no ceiling chase a dead card
forever and cost the merchant a fee each time, and a retry interval of zero
attempts the same card several times a minute, which is what a fraud system reads
as an attack.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

SLUG = "subscriptions"
CONFIG_FILENAME = "knight_config.json"

DEFAULTS: dict[str, Any] = {
    # Who takes the money. `manual` by default, deliberately: it keeps the
    # schedule, the periods and the ledger while moving no money at all, so a
    # store that installs this and configures nothing has a working subscription
    # book rather than a Feature quietly charging people.
    "provider": "manual",
    # The currency a subscription is priced in when the caller does not say.
    "currency": "IRR",
    # How many times a failed period is retried before the subscription is
    # marked unpaid. Three: enough for an expired card to be replaced over a
    # week, few enough that a merchant is not paying a failed-payment fee every
    # day for a month.
    "max_attempts": 3,
    # How long to wait before each retry, in days, by attempt number. Widening,
    # because a card that failed an hour ago fails again in an hour, and a
    # shopper needs time to notice the email.
    "retry_days": [1, 3, 7],
    # How far ahead of a period's start the store may generate its order. Zero
    # means on the day.
    "generate_ahead_hours": 0,
}

#: The only secret this Feature reads. Named here so that the manifest, the
#: provider and the configuration endpoint cannot drift apart on the spelling.
PAYMENT_SECRET = "payment_api_key"


def _from_file() -> dict[str, Any]:
    candidate = Path(__file__).resolve().parent.parent / CONFIG_FILENAME

    if not candidate.exists():
        return {}

    try:
        return json.loads(candidate.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # Loud, then carry on with defaults - and the defaults here charge
        # nobody, which is the only safe way for this particular Feature to fail
        # to read its own configuration.
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


def secret(name: str) -> str:
    """One secret's value, or an empty string when it was never delivered."""
    return str((_document().get("secrets") or {}).get(name) or "")


def provider() -> str:
    return str(value("provider") or "manual")


def currency() -> str:
    return str(value("currency") or "IRR")[:3].upper()


def max_attempts() -> int:
    """
    How many times one period may be charged before the merchant gives up.

    Clamped at both ends. One is the floor because a Feature that never retried
    would mark a subscription unpaid on a single network blip; ten is the ceiling
    because every attempt on a dead card can cost the merchant a fee, and a
    configuration of "999" is somebody who meant "keep trying" and did not think
    about the invoice.
    """
    try:
        attempts = int(value("max_attempts"))
    except (TypeError, ValueError):
        attempts = int(DEFAULTS["max_attempts"])

    return max(1, min(attempts, 10))


def retry_after_days(attempt: int) -> int:
    """
    How long to wait before the given attempt number.

    The last configured gap is reused past the end of the list, so a longer
    `max_attempts` than `retry_days` is a wider spacing rather than a crash. A
    gap of zero is refused for the reason in the module docstring: several
    attempts a minute on the same card is what a fraud system reads as an attack.
    """
    configured = value("retry_days")

    if not isinstance(configured, list) or not configured:
        configured = DEFAULTS["retry_days"]

    index = min(max(1, attempt), len(configured)) - 1

    try:
        days = int(configured[index])
    except (TypeError, ValueError):
        days = 1

    return max(1, min(days, 90))


def generate_ahead_hours() -> int:
    try:
        hours = int(value("generate_ahead_hours"))
    except (TypeError, ValueError):
        hours = 0

    return max(0, min(hours, 24 * 14))


def describe() -> dict[str, Any]:
    """
    What an operator needs to see, with nothing they must not.

    Secrets are reported as **present or absent and never by value**, which is
    the rule phase 15 set and the reason this function exists rather than callers
    reading `_document()`. A payment key echoed back by a debugging endpoint is a
    payment key in a log aggregator.
    """
    document = _document()

    return {
        "version": document.get("version", 0),
        "values": values(),
        "secretsPresent": sorted(
            name for name, held in (document.get("secrets") or {}).items() if held
        ),
    }
