"""
Reading this Feature's own configuration and secrets.

Same contract as every other Feature that needs configuration: KNIGHT's install
pipeline writes `knight_config.json` beside the delivered package, with
restrictive permissions, in the shape `{version, values, secrets}`
(`docs/feature-delivery.md` §9).

There is deliberately no environment-variable fallback for secrets. An env var is
visible to every process on the machine and to anything that dumps the
environment into an error report, and a Feature that quietly accepted one would
make the careful handling everywhere else pointless.

Nothing here logs a value.
"""

from __future__ import annotations

import json
import logging
from decimal import Decimal
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

SLUG = "ai-reports"
CONFIG_FILENAME = "knight_config.json"

DEFAULTS: dict[str, Any] = {
    # Sends nothing and costs nothing. See providers.LocalProvider for why this
    # is a real answer rather than a degraded one.
    "provider": "local",
    "price_per_1k_tokens": "0.01",
    "monthly_token_cap": 200_000,
    "monthly_cost_cap": "20.00",
}

SECRET_API_KEY = "model_api_key"


def _from_file() -> dict[str, Any]:
    candidate = Path(__file__).resolve().parent.parent / CONFIG_FILENAME

    if not candidate.exists():
        return {}

    try:
        return json.loads(candidate.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # Loud, then carry on with defaults — which means the local provider,
        # which spends nothing. A configuration this Feature cannot read must
        # never fail open onto a paid provider.
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


def caps() -> tuple[int, Decimal]:
    """The configured monthly caps, as the budget wants them."""
    return int(value("monthly_token_cap")), Decimal(str(value("monthly_cost_cap")))


def describe() -> dict[str, Any]:
    """What an operator needs to see, with nothing they must not."""
    document = _document()

    return {
        "version": document.get("version", 0),
        "values": values(),
        "secretsPresent": sorted(
            name for name, held in (document.get("secrets") or {}).items() if held
        ),
    }
