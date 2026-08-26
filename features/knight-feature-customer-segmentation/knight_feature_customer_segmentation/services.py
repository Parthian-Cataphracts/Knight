"""
Computing segments, and reading them.

The dependency is used the way the authoring guide says a dependency must be
used: through `analytics-core`'s **service surface**, never its models. That is
what makes the declared version range honest — analytics-core may change how it
stores events, and as long as `subjects_between` keeps its shape this Feature
does not care ([`feature-authoring.md`](../../../docs/feature-authoring.md)).

The import is deferred into the functions rather than done at module import, so a
store that has this installed while analytics-core is mid-upgrade answers one
request badly instead of failing to start.
"""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from decimal import Decimal

from django.db import transaction
from django.utils import timezone as django_timezone

from .models import Segment, SegmentMembership, SegmentRule, SegmentStatus


class AnalyticsUnavailable(RuntimeError):
    """
    Raised when `analytics-core` is not importable or is too old.

    An explicit failure rather than an empty result. A segmentation run that
    quietly produced no members because its dependency was missing would look
    exactly like a store with no customers, and a merchant would act on that.
    """


@dataclass(frozen=True)
class RunReport:
    """What one recomputation did."""

    segments: int
    members: int
    skipped: int = 0

    @property
    def did_nothing(self) -> bool:
        return self.segments == 0


def _analytics():
    """
    The dependency's service surface, checked before use.

    Both failure modes are distinguished, because they need different actions: a
    missing package is an install problem, and a package without
    `subjects_between` is a store running analytics-core 1.0.x when this Feature
    declared it needs 1.1.0 - a resolver failure rather than a delivery one.
    """
    try:
        from knight_feature_analytics_core import services as analytics
    except ImportError as error:  # pragma: no cover - depends on the install
        raise AnalyticsUnavailable(
            "analytics-core is not installed on this store, and customer-segmentation "
            "cannot compute a segment without it."
        ) from error

    if not hasattr(analytics, "subjects_between"):
        raise AnalyticsUnavailable(
            "analytics-core on this store is older than 1.1.0 and has no per-subject "
            "aggregation. customer-segmentation declares >=1.1.0 for this reason."
        )

    return analytics


@transaction.atomic
def recalculate(*, at: datetime | None = None) -> RunReport:
    """
    Recomputes every active segment.

    One transaction for the whole run. A campaign screen reading a segment
    half-way through a recomputation would show a list that is neither the old
    one nor the new one, and a merchant would send to it.

    Paused segments are left exactly as they were rather than emptied. Pausing a
    segment means "stop maintaining this", not "delete what it found".
    """
    analytics = _analytics()
    moment = at or django_timezone.now()

    segments = list(Segment.objects.filter(status=SegmentStatus.ACTIVE))
    members = 0

    for segment in segments:
        window_start = moment - timedelta(days=segment.window_days)

        activity = analytics.subjects_between(
            window_start,
            moment,
            name=segment.event_name or None,
        )

        matched = [row for row in activity if _matches(segment, row, moment)]

        # Replaced rather than merged: somebody who no longer qualifies has to
        # leave the segment, and an upsert-only run would never remove anybody.
        SegmentMembership.objects.filter(segment=segment).delete()

        SegmentMembership.objects.bulk_create(
            [
                SegmentMembership(
                    segment=segment,
                    subject=row.subject,
                    events=row.events,
                    total_value=row.total_value,
                    last_seen_at=row.last_seen,
                    computed_at=moment,
                )
                for row in matched
            ]
        )

        segment.last_computed_at = moment
        segment.save(update_fields=["last_computed_at", "updated_at"])

        members += len(matched)

    return RunReport(segments=len(segments), members=members)


def _matches(segment: Segment, row, moment: datetime) -> bool:
    """
    Whether one subject belongs in one segment.

    Every branch reads the thresholds its own rule owns and ignores the rest,
    which is why they are separate columns: a VIP threshold accidentally applied
    to a dormancy rule is a segment nobody can explain.
    """
    if segment.rule == SegmentRule.NEW:
        if row.first_seen is None:
            return False

        return (moment - row.first_seen) <= timedelta(days=segment.new_within_days)

    if segment.rule == SegmentRule.FREQUENT:
        return row.events >= segment.minimum_events

    if segment.rule == SegmentRule.HIGH_VALUE:
        return row.total_value >= segment.minimum_value

    if segment.rule == SegmentRule.VIP:
        # Both, deliberately. A customer who ordered once for a large amount is
        # high-value; a VIP is somebody who keeps coming back and spends.
        return row.events >= segment.minimum_events and row.total_value >= segment.minimum_value

    if segment.rule == SegmentRule.DORMANT:
        if row.last_seen is None:
            return False

        return (moment - row.last_seen) >= timedelta(days=segment.dormant_after_days)

    # An unknown rule matches nobody. A stored value this code does not
    # recognise is a deployment mismatch, and guessing at it would put real
    # customers in a segment by accident.
    return False


def members_of(slug: str, *, limit: int = 100, offset: int = 0) -> list[dict]:
    """
    Who is in one segment, highest value first, as plain data.

    Bounded: a segment on a busy store is a long list, and the caller asking for
    it is usually rendering a page.
    """
    limit = max(1, min(limit, 500))

    rows = SegmentMembership.objects.filter(segment__slug=slug).order_by("-total_value", "subject")[
        offset : offset + limit
    ]

    return [
        {
            "subject": row.subject,
            "events": row.events,
            "totalValue": str(row.total_value),
            "lastSeenAt": row.last_seen_at.isoformat() if row.last_seen_at else None,
        }
        for row in rows
    ]


def summary() -> list[dict]:
    """
    Every segment with its size, for an overview screen.

    Counted in one query rather than per segment: five segments is five queries
    the moment somebody writes the obvious loop.
    """
    from django.db.models import Count

    rows = (
        Segment.objects.annotate(size=Count("memberships"))
        .order_by("name")
        .values("name", "slug", "rule", "status", "size", "last_computed_at")
    )

    return [
        {
            "name": row["name"],
            "slug": row["slug"],
            "rule": row["rule"],
            "status": row["status"],
            "size": row["size"],
            "lastComputedAt": row["last_computed_at"].isoformat() if row["last_computed_at"] else None,
        }
        for row in rows
    ]


def segments_for(subject: str) -> list[str]:
    """
    Which segments one customer is in.

    The lookup marketing automation makes per recipient, so it returns slugs and
    nothing else.
    """
    return list(
        SegmentMembership.objects.filter(subject=subject)
        .order_by("segment__name")
        .values_list("segment__slug", flat=True)
    )


def ensure_defaults() -> int:
    """
    Creates the five segments a merchant expects, if they are absent.

    Called from the install's configure step. Defaults rather than an empty
    screen: a segmentation feature that installs with nothing in it puts the
    burden of inventing five definitions on somebody who bought it to save time.
    """
    defaults = [
        ("New customers", "new-customers", SegmentRule.NEW, {"new_within_days": 30}),
        ("Frequent customers", "frequent-customers", SegmentRule.FREQUENT, {"minimum_events": 3}),
        (
            "High-value customers",
            "high-value-customers",
            SegmentRule.HIGH_VALUE,
            {"minimum_value": Decimal("1000000")},
        ),
        (
            "VIP customers",
            "vip-customers",
            SegmentRule.VIP,
            {"minimum_events": 5, "minimum_value": Decimal("2000000")},
        ),
        (
            "Dormant customers",
            "dormant-customers",
            SegmentRule.DORMANT,
            {"dormant_after_days": 60, "window_days": 365},
        ),
    ]

    created = 0

    for name, slug, rule, overrides in defaults:
        _, made = Segment.objects.get_or_create(
            slug=slug, defaults={"name": name, "rule": rule, **overrides}
        )

        if made:
            created += 1

    return created
