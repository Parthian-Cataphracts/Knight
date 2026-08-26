"""
What this feature does, as functions other features call.

This module is the published surface. `knight-feature-analytics-reports` depends
on it rather than on the models, so the storage can change without breaking it —
which is the point of depending on a version range instead of a table.
"""

from __future__ import annotations

from collections import Counter
from dataclasses import dataclass
from datetime import date, datetime, timezone
from decimal import Decimal

from django.db.models import Count, F, FloatField, Max, Min, Sum
from django.db.models.fields.json import KeyTextTransform
from django.db.models.functions import Cast

from .models import AnalyticsEvent, DailyRollup


def record(
    name: str,
    payload: dict | None = None,
    occurred_at: datetime | None = None,
    subject: str = "",
) -> AnalyticsEvent:
    """
    Records one event.

    `subject` is who the event is about, and is optional: an event without one is
    a perfectly ordinary event about the store rather than about a customer.
    Added in 1.1.0 and positionally compatible with every 1.0.x call, which is
    what makes this a minor version rather than a breaking one.
    """
    return AnalyticsEvent.objects.create(
        name=name,
        occurred_at=occurred_at or datetime.now(timezone.utc),
        payload=payload or {},
        subject=subject or "",
    )


@dataclass(frozen=True)
class SubjectActivity:
    """
    One subject's activity over a window, as plain data.

    What a segmentation rule needs and nothing more. `total_value` is summed from
    the payload's `value` key where there is one, because how much a customer has
    spent is the question every value-based segment asks, and the only version of
    it this feature can answer without knowing what an order is.
    """

    subject: str
    events: int
    first_seen: datetime | None
    last_seen: datetime | None
    total_value: Decimal

    @property
    def has_value(self) -> bool:
        return self.total_value > Decimal("0")


def subjects_between(
    start: datetime,
    end: datetime,
    *,
    name: str | None = None,
    minimum_events: int = 1,
) -> list[SubjectActivity]:
    """
    Per-subject activity over a window, most recently active first.

    New in 1.1.0. Aggregated in the database rather than in Python: a store with
    a year of events has more of them than a worker should hold in memory, and a
    segmentation run that loaded them all would be the reason somebody switched
    segmentation off.

    Events with no subject are excluded rather than grouped under a blank one. A
    row for the empty string is not a customer, and every caller would otherwise
    have to remember to drop it.
    """
    queryset = AnalyticsEvent.objects.exclude(subject="").filter(
        occurred_at__gte=start, occurred_at__lt=end
    )

    if name is not None:
        queryset = queryset.filter(name=name)

    rows = (
        queryset.values("subject")
        .annotate(
            events=Count("id"),
            first_seen=Min("occurred_at"),
            last_seen=Max("occurred_at"),
            # Cast through text because the payload is a document: a caller may
            # have written a number or a string, and a sum over mixed types is a
            # database error rather than a zero.
            total_value=Sum(Cast(KeyTextTransform("value", "payload"), FloatField())),
        )
        .filter(events__gte=max(1, minimum_events))
        .order_by(F("last_seen").desc())
    )

    return [
        SubjectActivity(
            subject=row["subject"],
            events=row["events"],
            first_seen=row["first_seen"],
            last_seen=row["last_seen"],
            total_value=Decimal(str(row["total_value"] or 0)).quantize(Decimal("0.01")),
        )
        for row in rows
    ]


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
