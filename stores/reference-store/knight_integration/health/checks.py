"""
What "healthy" means for this store.

Each check is cheap, bounded, and about a dependency this store cannot serve
without. None of them touch a business table: the health endpoint has to stay
fast enough to poll every thirty seconds, and a slow product query would make
the store look unhealthy for reasons that have nothing to do with health
(docs/store-integration.md §5).
"""

from __future__ import annotations

import time
from typing import Any

HEALTHY = "healthy"
DEGRADED = "degraded"
UNHEALTHY = "unhealthy"

#: Worst status wins when the individual checks are rolled up.
_SEVERITY = {HEALTHY: 0, DEGRADED: 1, UNHEALTHY: 2}


def run_all() -> tuple[str, dict[str, Any]]:
    """Runs every dependency check and returns the overall status plus the detail."""
    dependencies = {
        "database": check_database(),
        "cache": check_cache(),
    }

    overall = HEALTHY
    for result in dependencies.values():
        if _SEVERITY[result["status"]] > _SEVERITY[overall]:
            overall = result["status"]

    return overall, dependencies


def check_database() -> dict[str, Any]:
    from django.db import connection

    started = time.perf_counter()

    try:
        with connection.cursor() as cursor:
            cursor.execute("SELECT 1")
            cursor.fetchone()
    except Exception as exc:  # noqa: BLE001 - any failure here is the answer
        return {"status": UNHEALTHY, "detail": _summarise(exc)}

    return {"status": HEALTHY, "latencyMs": _elapsed_ms(started)}


def check_cache() -> dict[str, Any]:
    from django.core.cache import cache

    started = time.perf_counter()
    key = "knight:health:probe"

    try:
        cache.set(key, "1", timeout=10)
        if cache.get(key) != "1":
            # A cache that accepts writes and returns nothing is worse than one
            # that is down: the store would keep handshaking on every request.
            return {"status": DEGRADED, "detail": "The cache did not return what was written to it."}
    except Exception as exc:  # noqa: BLE001
        return {"status": DEGRADED, "detail": _summarise(exc)}

    return {"status": HEALTHY, "latencyMs": _elapsed_ms(started)}


def _elapsed_ms(started: float) -> int:
    return int((time.perf_counter() - started) * 1000)


def _summarise(exception: Exception) -> str:
    """
    One line, and never the exception's full text: a database error message
    routinely contains the connection string.
    """
    return f"{type(exception).__name__} while checking the dependency."
