"""
What this feature does, as functions other features call.

This module is the published surface. `knight-feature-analytics-reports` depends
on it rather than on the models, so the storage can change without breaking it —
which is the point of depending on a version range instead of a table.
"""

from __future__ import annotations

from collections import Counter
from datetime import date, datetime, timezone

from .models import AnalyticsEvent, DailyRollup


def record(name: str, payload: dict | None = None, occurred_at: datetime | None = None) -> AnalyticsEvent:
    """Records one event."""
    return AnalyticsEvent.objects.create(
        name=name,
        occurred_at=occurred_at or datetime.now(timezone.utc),
        payload=payload or {},
    )


def roll_up(day: date) -> int:
    """
    Collapses one day's events into counters.

    Idempotent: running it twice for the same day produces the same rollups
    rather than doubling them. A scheduled job that cannot safely be re-run is a
    job nobody can retry after an outage.
    """
    events = AnalyticsEvent.objects.filter(occurred_at__date=day).values_list("name", flat=True)
    counts = Counter(events)

    for name, count in counts.items():
        DailyRollup.objects.update_or_create(day=day, name=name, defaults={"count": count})

    return len(counts)


def counts_for(day: date) -> dict[str, int]:
    """The rollup for one day, as a plain mapping."""
    return dict(DailyRollup.objects.filter(day=day).values_list("name", "count"))
