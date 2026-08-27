"""
The one table the drill writes to, at 1.0.0.

Deliberately dull: what is being tested is the delivery path, not this Feature.
There is no `note` column here, and 1.1.0 adds one — which is what makes an
upgrade that did nothing and a rollback that did nothing both *visible*. Phase
18's drill moved between two versions whose migrations were identical, so it
could see neither.
"""

from django.db import models


class DrillRecord(models.Model):
    """A row that has to survive an upgrade and a rollback."""

    reference = models.CharField(max_length=64, unique=True)
    written_at = models.DateTimeField(auto_now_add=True)

    class Meta:
        db_table = "knight_drill_record"
        ordering = ("reference",)

    def __str__(self) -> str:
        return self.reference
