"""
Reading this Feature's own configuration.

The same contract as every other Feature that needs configuration: KNIGHT's
install pipeline writes `knight_config.json` beside the delivered package, in the
shape `{version, values, secrets}` (`docs/feature-delivery.md` §9).

There are no secrets here, and that is worth saying rather than leaving to be
noticed. This Feature talks to nobody: no provider, no API key, no third party.
Everything it does happens inside the store's own database.

Every value read here is clamped rather than trusted. A restaurant's
configuration is edited by a manager at eleven at night, and each of these has a
setting that would quietly ruin a service: a zero-minute hold sells the same slot
twice, a day-long one takes the diary out of use, and a throughput of zero makes
every promise infinite.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

SLUG = "restaurant-operations"
CONFIG_FILENAME = "knight_config.json"

DEFAULTS: dict[str, Any] = {
    # How long a pickup window is. Fifteen minutes is what a shopper reads as a
    # time rather than a range, and what a kitchen can actually distinguish.
    "slot_minutes": 15,
    # How much work the kitchen will accept in one window, in the same load units
    # the prep profiles use.
    "slot_capacity_units": 20,
    # How long a chosen pickup time is held for a checkout that has not paid.
    # Ten minutes: long enough to type card details, short enough that an
    # abandoned basket gives the last table-time back while people are still
    # ordering.
    "hold_minutes": 10,
    # How far ahead times are offered. A restaurant taking orders for next month
    # is a restaurant promising a kitchen it has not staffed yet.
    "booking_horizon_hours": 48,
    # What an unprofiled item is assumed to take. Deliberately not zero: an
    # unmeasured dish that counted as instant would make the kitchen look empty
    # exactly when it is handling something nobody has timed.
    "default_prep_minutes": 10,
    "default_load_units": 1,
    # What the kitchen clears in an hour when no stations have been described.
    "throughput_units_per_hour": 60,
    # After this long, a table session nobody closed is treated as abandoned. Set
    # past the longest plausible meal: closing a session under a party still
    # eating would let somebody else be seated on top of them.
    "abandon_after_hours": 6,
}


def _from_file() -> dict[str, Any]:
    candidate = Path(__file__).resolve().parent.parent / CONFIG_FILENAME

    if not candidate.exists():
        return {}

    try:
        return json.loads(candidate.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # Loud, then carry on with defaults. The defaults are the conservative
        # end of every one of these: a shorter hold, a nearer horizon, and a
        # kitchen assumed to be slower than it probably is.
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


def _int(key: str, *, low: int, high: int) -> int:
    """One clamped integer, and the only way any of these are read."""
    try:
        number = int(value(key))
    except (TypeError, ValueError):
        number = int(DEFAULTS[key])

    return max(low, min(number, high))


def slot_minutes() -> int:
    return _int("slot_minutes", low=5, high=240)


def slot_capacity() -> int:
    return _int("slot_capacity_units", low=1, high=10_000)


def hold_minutes() -> int:
    return _int("hold_minutes", low=1, high=24 * 60)


def booking_horizon_hours() -> int:
    return _int("booking_horizon_hours", low=1, high=24 * 90)


def default_prep_minutes() -> int:
    return _int("default_prep_minutes", low=0, high=24 * 60)


def default_load_units() -> int:
    return _int("default_load_units", low=0, high=1_000)


def throughput_units_per_hour() -> int:
    return _int("throughput_units_per_hour", low=1, high=100_000)


def abandon_after_hours() -> int:
    return _int("abandon_after_hours", low=1, high=24 * 7)
