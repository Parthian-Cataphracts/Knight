"""
Reading this Feature's own configuration and secrets.

KNIGHT's install pipeline writes `knight_config.json` beside the delivered
package, with restrictive permissions, in the shape
`{version, values, secrets}` (`docs/feature-delivery.md` §9). This is how a
Feature reads it.

Three sources, in this order, and the order is the point:

1. **The file the installer wrote.** The real path in production.
2. **`settings.KNIGHT_FEATURE_CONFIG`**, keyed by slug. For a developer working
   out of the source tree, where no installer has run.
3. **The manifest defaults**, hard-coded here.

A secret is only ever read from source 1 or 2. There is deliberately no
environment-variable fallback for secrets: an env var is visible to every
process on the machine and to anything that dumps the environment into an error
report, and a Feature that quietly accepted one would make the careful handling
everywhere else pointless.

Nothing here logs a value. `describe()` exists so an operator can see *which*
secrets are present without seeing any of them.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

SLUG = "marketing-automation"
CONFIG_FILENAME = "knight_config.json"

#: The manifest's `configuration.defaults`, repeated here because a Feature must
#: work on a store where the installer wrote nothing — a developer's checkout,
#: or a store whose configuration has never been set.
DEFAULTS: dict[str, Any] = {
    "provider": "recording",
    "from_email": "",
    "from_name": "",
    "maximum_per_run": 200,
    "unsubscribe_url": "",
}

#: Named, never valued. The manifest declares the same name and no value.
SECRET_API_KEY = "email_api_key"


def _from_file() -> dict[str, Any]:
    """
    The installer's document, if this package was delivered rather than pip-installed.

    The config sits beside the package directory, because that is where the
    install step puts both. A missing file is the ordinary case in development
    and must not be an error.
    """
    candidate = Path(__file__).resolve().parent.parent / CONFIG_FILENAME

    if not candidate.exists():
        return {}

    try:
        return json.loads(candidate.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        # Loud, and then carry on with defaults. A campaign that refused to run
        # because its configuration file was unreadable would be a silent outage;
        # one that runs with the `recording` provider sends nothing and says so.
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

    if document:
        return document

    return _from_settings()


def values() -> dict[str, Any]:
    """The configuration, with defaults filled in for anything unset."""
    document = _document()

    return {**DEFAULTS, **(document.get("values") or {})}


def value(key: str, default: Any = None) -> Any:
    return values().get(key, DEFAULTS.get(key, default))


def secret(name: str) -> str:
    """
    One secret's value, or an empty string when it was never delivered.

    Empty rather than raising, because the caller has to decide what an absent
    secret means. For this Feature it means the API provider cannot send, which
    is reported per message rather than by refusing to run at all.
    """
    return str((_document().get("secrets") or {}).get(name) or "")


def describe() -> dict[str, Any]:
    """
    What an operator needs to see, with nothing they must not.

    Secrets are reported as names and a boolean. A support conversation needs to
    know whether the key arrived, never what it is.
    """
    document = _document()

    return {
        "version": document.get("version", 0),
        "values": values(),
        "secretsPresent": sorted(
            name for name, held in (document.get("secrets") or {}).items() if held
        ),
    }
