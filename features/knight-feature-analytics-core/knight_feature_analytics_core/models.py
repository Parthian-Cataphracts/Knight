"""
The event table.

Owned entirely by this feature. Nothing in the store imports these models and
this feature imports nothing from the store, so uninstalling it cannot break the
store's checkout.
"""

from django.db import models


class AnalyticsEvent(models.Model):
    """One thing that happened in the store, as this feature saw it."""

    name = models.CharField(max_length=100, db_index=True)
    occurred_at = models.DateTimeField(db_index=True)

    # A document rather than columns: the shape of an event is the caller's
    # business, and a feature needing a migration for every new event type is a
    # feature nobody can extend.
    payload = models.JSONField(default=dict, blank=True)

    # Who or what the event is about - a customer reference, usually. Added in
    # 1.1.0 for `customer-segmentation`, which has to group events per customer
    # and cannot do that from a payload key nothing indexes.
    #
    # Nullable in effect, and it stays that way: every event recorded before
    # 1.1.0 has no subject, and backfilling one would mean inventing a fact. A
    # caller that wants per-subject aggregation starts passing it and gets
    # answers from that point on, which is the honest shape of adding a
    # dimension to a log.
    #
    # An opaque string rather than an integer id. This feature has no idea what a
    # customer looks like in the store above it, and must not acquire one.
    # No db_index here: the composite index below starts with `subject`, so a
    # lookup by subject alone already uses it. Declaring both would add a second
    # b-tree and a varchar_pattern_ops index to maintain on every insert, for a
    # query plan that does not change.
    subject = models.CharField(max_length=200, blank=True, default="")

    class Meta:
        db_table = "knight_analytics_event"
        indexes = [
            models.Index(fields=["name", "occurred_at"], name="knight_anal_name_occ_idx"),
            # The index that makes per-subject aggregation over a window cheap.
            models.Index(fields=["subject", "occurred_at"], name="knight_anal_subj_occ_idx"),
        ]
        ordering = ["-occurred_at"]

    def __str__(self) -> str:
        return f"{self.name} at {self.occurred_at:%Y-%m-%d %H:%M}"


class DailyRollup(models.Model):
    """A day's count for one event name, so a report never scans the raw table."""

    day = models.DateField(db_index=True)
    name = models.CharField(max_length=100)
    count = models.PositiveIntegerField(default=0)

    class Meta:
        db_table = "knight_analytics_daily_rollup"
        unique_together = [("day", "name")]
        ordering = ["-day", "name"]

    def __str__(self) -> str:
        return f"{self.name} x{self.count} on {self.day}"
