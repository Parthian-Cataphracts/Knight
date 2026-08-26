"""
Reading this Feature's own configuration.

The same contract as every other Feature that needs configuration: KNIGHT's
install pipeline writes `knight_config.json` beside the delivered package, in the
shape `{version, values, secrets}` (`docs/feature-delivery.md` §9).

There are no secrets here, and that is worth saying rather than leaving to be
noticed. This Feature talks to nobody: no provider, no API key, no third party.
Everything it does happens inside the store's own database.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

SLUG = "advanced-inventory"
CONFIG_FILENAME = "knight_config.json"

DEFAULTS: dict[str, Any] = {
    # How long stock stays held for a basket that has not paid.
    "reservation_minutes": 20,
    # What a store with one shop records everything against.
    "default_location": "",
    # Whether the daily sweep raises alerts for items nobody has set a reorder
    # point on. Off: an item whose reorder point is zero is an item nobody has
    # said "low" means anything for, and alerting on it fills the list with
    # things that were never being watched.
    "alert_without_reorder_point": False,
}


def _from_file() -> dict[str, Any]:
    candidate = Path(__file__).resolve().parent.parent / CONFIG_FILENAME

    if not candidate.exists():
        return {}

    try:
        return json.loads(candidate.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # Loud, then carry on with defaults. The defaults are the conservative
        # end of every one of these: a shorter hold and fewer alerts.
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


def hold_minutes() -> int:
    """
    The configured hold, clamped to something a checkout could plausibly need.

    Clamped rather than trusted, because both ends of the range are a real
    failure. A hold of zero makes reservations pointless and sells the same item
    twice; a hold of a week makes an abandoned basket indistinguishable from
    being out of stock.
    """
    try:
        minutes = int(value("reservation_minutes"))
    except (TypeError, ValueError):
        minutes = int(DEFAULTS["reservation_minutes"])

    return max(1, min(minutes, 24 * 60))


def default_location() -> str:
    return str(value("default_location") or "")


def alerts_without_reorder_point() -> bool:
    return bool(value("alert_without_reorder_point"))
