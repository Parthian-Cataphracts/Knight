"""
Reading this Feature's own configuration.

The same contract as every other Feature that needs configuration: KNIGHT's
install pipeline writes `knight_config.json` beside the delivered package, in the
shape `{version, values, secrets}` (`docs/feature-delivery.md` §9).

There are no secrets here, and that is worth saying rather than leaving to be
noticed. This Feature talks to nobody: no provider, no API key, no third party.
Everything it does happens inside the store's own database.

Both settings default to the **permissive** reading, which is the opposite of the
house habit and deliberate. Every other Feature in this catalogue defaults to the
conservative end because the failure there is doing too much — spending money,
sending an email, promising stock. Here the failure is doing too little: a
merchant installs this on a Tuesday morning, has entered no opening hours and
described no branches, and a conservative default would mean every order in the
shop stopped being routable at the moment of the install.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

SLUG = "multi-location"
CONFIG_FILENAME = "knight_config.json"

DEFAULTS: dict[str, Any] = {
    # Whether a location with no opening hours at all counts as open. On: nobody
    # has entered any hours on the day this Feature is installed, and a merchant
    # whose every branch silently stopped taking orders would rightly call that a
    # broken release. A merchant who enters hours has said what they mean.
    "open_without_hours": True,
    # Whether an order that matched no rule and has no default may go to the only
    # branch that is open. On: a merchant with one open branch has not made a
    # routing decision worth asking them about, and refusing would break the
    # single-site case this Feature is supposed to leave alone.
    "route_to_the_only_open_location": True,
}


def _from_file() -> dict[str, Any]:
    candidate = Path(__file__).resolve().parent.parent / CONFIG_FILENAME

    if not candidate.exists():
        return {}

    try:
        return json.loads(candidate.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # Loud, then carry on with defaults — which here means carrying on
        # routing orders rather than refusing them.
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


def open_without_hours() -> bool:
    return bool(value("open_without_hours"))


def route_to_the_only_open_location() -> bool:
    return bool(value("route_to_the_only_open_location"))
