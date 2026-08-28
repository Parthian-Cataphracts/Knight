"""
This service's own configuration, per store.

The same interface the Feature package had — ``provider()``, ``currency()``,
``max_attempts()``, ``retry_after_days()``, ``secret()`` — so the domain moved
across unchanged. What is behind it is completely different, and the difference
is the whole architecture.

**In 1.x** configuration arrived through the install pipeline: KNIGHT wrote
``knight_config.json`` beside the delivered package and this module read it. The
values were per-store because the *installation* was per-store.

**Here** one deployment serves every store, so per-store configuration is a
column rather than a file. A store's settings are read from its own row; the
service-wide defaults come from the environment.

Every numeric value is still clamped rather than trusted, and for the reason the
original gave: a subscriptions service misconfigured at eleven at night is one
that charges people. Retries with no ceiling chase a dead card forever and cost
the merchant a fee each time; a retry interval of zero attempts the same card
several times a minute, which is what a fraud system reads as an attack.
"""

from __future__ import annotations

import logging
import os
from typing import Any

logger = logging.getLogger(__name__)

#: The current store, set for the length of one request or one billing pass.
#:
#: Threading it through every call in `services.py` would have meant changing
#: sixty signatures to reach four reads; this keeps the domain's own code
#: identical to the version that shipped in 1.x. It is set by `use()` and always
#: in a `with`, so a request that raised cannot leave the next one reading the
#: wrong shop's settings.
_current: Any = None


class use:
    """
    Reads this store's settings for the duration of a block.

    A context manager rather than a plain setter, because the failure mode of
    forgetting to unset would be one store billed under another's provider —
    and that is the kind of bug that is found by a merchant rather than by a
    test.
    """

    def __init__(self, store) -> None:
        self._store = store
        self._previous = None

    def __enter__(self):
        global _current
        self._previous = _current
        _current = self._store
        return self._store

    def __exit__(self, *_) -> None:
        global _current
        _current = self._previous


def values() -> dict[str, Any]:
    """This store's settings, or the service's defaults when none is current."""
    if _current is None:
        return {}

    return dict(getattr(_current, "settings", None) or {})


def value(key: str, default: Any = None) -> Any:
    return values().get(key, default)


def secret(name: str) -> str:
    """
    A payment credential for the current store.

    Per store, always. One provider key shared across a fleet would mean every
    merchant's charges landing in one account, which is not a bug anybody
    recovers from quietly.

    Read from the store row, and never logged, never returned by an endpoint,
    never in an error message — the arrangement the Feature established in phase
    15 and the one thing about this module that did not change.
    """
    if _current is None:
        return ""

    return str((getattr(_current, "secrets", None) or {}).get(name) or "")


def provider() -> str:
    """
    Who takes the money. `manual` charges nobody.

    The default is deliberate and is the same one 1.x shipped with: a store that
    has not chosen a provider must not be charging anybody, and the safe failure
    is a period that stays unpaid rather than a charge nobody authorised.
    """
    return str(value("provider") or os.environ.get("SUBSCRIPTIONS_DEFAULT_PROVIDER", "manual")).strip() or "manual"


def currency() -> str:
    return (str(value("currency") or os.environ.get("SUBSCRIPTIONS_DEFAULT_CURRENCY", "IRR")).strip().upper() or "IRR")[:3]


def max_attempts() -> int:
    """
    How many times one period may be charged before it is given up on.

    Clamped to 1..10. Zero would mean a card that failed once is never retried;
    unbounded would chase a dead card forever, at a fee each time.
    """
    try:
        declared = int(value("retry_attempts", os.environ.get("SUBSCRIPTIONS_RETRY_ATTEMPTS", 3)))
    except (TypeError, ValueError):
        logger.warning("retry_attempts is not a number; using 3.")
        return 3

    return max(1, min(10, declared))


def retry_after_days(attempt: int) -> int:
    """
    How long to wait before the next attempt, in days.

    Doubling, and floored at one. A retry measured in hours is the same card
    failing again an hour later, and a retry of zero days is several attempts a
    minute on a card that has already declined.
    """
    try:
        base = int(value("retry_after_days", os.environ.get("SUBSCRIPTIONS_RETRY_AFTER_DAYS", 3)))
    except (TypeError, ValueError):
        base = 3

    base = max(1, min(30, base))

    return min(30, base * max(1, 2 ** max(0, int(attempt) - 1)))


def generate_ahead_hours() -> int:
    """How far ahead of its due time a period may be opened."""
    try:
        declared = int(value("generate_ahead_hours", os.environ.get("SUBSCRIPTIONS_AHEAD_HOURS", 0)))
    except (TypeError, ValueError):
        return 0

    return max(0, min(72, declared))


def describe() -> dict[str, Any]:
    """
    What this store is configured with, with nothing secret in it.

    Safe to return from an endpoint and safe to log, which is why the secrets
    are read through `secret()` and never appear here.
    """
    return {
        "provider": provider(),
        "currency": currency(),
        "retryAttempts": max_attempts(),
        "retryAfterDays": retry_after_days(1),
        "generateAheadHours": generate_ahead_hours(),
    }
