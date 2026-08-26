"""
Customer segments, and who is in them.

The first Feature in the catalogue whose data comes from **another Feature**
rather than from the base store, and that is the point of it in phase 13. It
groups customers by what they have done, and the only place it can learn what
they have done is `analytics-core` — a Feature may not import store business
code, so the order table is not available to it and never will be.

That makes `analytics-core >=1.1.0` a real dependency rather than a decorative
one: 1.0.x has no per-subject aggregation at all, so this Feature is not merely
better with it, it cannot function without it. Installing this on a store whose
analytics is on 1.0.x must upgrade that first, in that order — which is exactly
the resolver behaviour this Feature exists to exercise
([`adr/0017`](../../../docs/adr/0017-feature-compatibility-and-dependencies.md)).

A customer is an opaque **subject string**, the same one the store passes when it
records an event. This package has no idea what a customer row looks like and
must not acquire one.
"""

from decimal import Decimal

from django.core.exceptions import ValidationError
from django.core.validators import MinValueValidator
from django.db import models
from django.utils import timezone


class SegmentRule(models.TextChoices):
    """
    How membership is decided.

    A closed list rather than a stored expression. A rule language would be a
    query engine with a migration path, and the five segments a merchant
    actually asks for are these.
    """

    NEW = "New", "New customers"
    FREQUENT = "Frequent", "Frequent customers"
    HIGH_VALUE = "HighValue", "High-value customers"
    VIP = "VIP", "VIP customers"
    DORMANT = "Dormant", "Dormant customers"


class SegmentStatus(models.TextChoices):
    ACTIVE = "Active", "Active"
    PAUSED = "Paused", "Paused"


class Segment(models.Model):
    """
    A definition, not a list. Who is in it is `SegmentMembership`, recomputed.

    The thresholds are columns rather than a JSON blob because every one of them
    is read by the rule that owns it, and a typo in a JSON key is a segment that
    silently matches everybody.
    """

    name = models.CharField(max_length=150)
    slug = models.SlugField(max_length=150, unique=True)
    description = models.TextField(max_length=1000, blank=True, default="")

    rule = models.CharField(max_length=20, choices=SegmentRule)
    status = models.CharField(max_length=16, choices=SegmentStatus, default=SegmentStatus.ACTIVE)

    # The window the rule looks back over. Every rule needs one: "frequent" with
    # no window is just "has ever ordered twice", which is not a segment.
    window_days = models.PositiveIntegerField(default=90)

    # Used by FREQUENT and VIP.
    minimum_events = models.PositiveIntegerField(default=2)

    # Used by HIGH_VALUE and VIP.
    minimum_value = models.DecimalField(
        max_digits=14, decimal_places=2, default=Decimal("0"),
        validators=[MinValueValidator(Decimal("0"))],
    )

    # Used by DORMANT: no activity for at least this long. Separate from
    # window_days, which bounds how far back the scan goes at all.
    dormant_after_days = models.PositiveIntegerField(default=60)

    # Used by NEW: first seen within this many days.
    new_within_days = models.PositiveIntegerField(default=30)

    # Which event counts. Empty means every event, which is the right default
    # for "frequent" and the wrong one for "high value" — hence configurable.
    event_name = models.CharField(max_length=100, blank=True, default="")

    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)
    last_computed_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_segment"
        ordering = ("name",)

    def clean(self) -> None:
        super().clean()

        if self.rule == SegmentRule.DORMANT and self.dormant_after_days >= self.window_days:
            # Otherwise the window ends before the dormancy threshold begins and
            # the segment can never match anybody — a segment that is always
            # empty looks like broken data rather than a bad definition.
            raise ValidationError(
                {"dormant_after_days": "Dormancy must be shorter than the window it is measured in."}
            )

    @property
    def is_live(self) -> bool:
        return self.status == SegmentStatus.ACTIVE

    def __str__(self) -> str:
        return f"{self.name} ({self.rule})"


class SegmentMembership(models.Model):
    """
    One customer's place in one segment, as of the last recomputation.

    Stored rather than computed per request. A segment is read on a campaign
    screen and by marketing automation, and recomputing five rules over a year
    of events every time somebody opens a page is how a reporting feature
    becomes the reason a store is slow.

    `computed_at` is on the row rather than only on the segment so that a
    half-finished run is visible as one: rows from two different runs sitting
    side by side is a fact somebody will need to see.
    """

    segment = models.ForeignKey(Segment, on_delete=models.CASCADE, related_name="memberships")

    # The same opaque string the store passes to analytics-core as `subject`.
    subject = models.CharField(max_length=200)

    # What the rule measured, kept for display: a merchant looking at a VIP list
    # wants to know why each name is on it.
    events = models.PositiveIntegerField(default=0)
    total_value = models.DecimalField(max_digits=14, decimal_places=2, default=Decimal("0"))
    last_seen_at = models.DateTimeField(null=True, blank=True)

    computed_at = models.DateTimeField(default=timezone.now)

    class Meta:
        db_table = "knight_segment_membership"
        ordering = ("-total_value", "subject")
        indexes = [
            models.Index(fields=["segment", "-total_value"], name="knight_seg_mem_value_idx"),
        ]
        constraints = [
            models.UniqueConstraint(
                fields=["segment", "subject"],
                name="knight_segment_one_row_per_subject",
            ),
        ]

    def __str__(self) -> str:
        return f"{self.subject} in {self.segment_id}"
