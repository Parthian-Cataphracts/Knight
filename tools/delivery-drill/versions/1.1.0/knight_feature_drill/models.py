"""
The one table the drill writes to, at 1.1.0.

The same table as 1.0.0 plus `note`. That column is the whole difference between
the two versions, and it is what makes the drill's upgrade and rollback real: an
upgrade has a migration to apply, a rollback has one to reverse, and both are
observable by looking at the table rather than by trusting a version number.
"""

from django.db import models


class DrillRecord(models.Model):
    """A row that has to survive an upgrade and a rollback."""

    reference = models.CharField(max_length=64, unique=True)
    written_at = models.DateTimeField(auto_now_add=True)

    #: Added by 1.1.0. A store on 1.0.0 does not have this column at all.
    note = models.CharField(max_length=200, blank=True, default="")

    class Meta:
        db_table = "knight_drill_record"
        ordering = ("reference",)

    def __str__(self) -> str:
        return self.reference
