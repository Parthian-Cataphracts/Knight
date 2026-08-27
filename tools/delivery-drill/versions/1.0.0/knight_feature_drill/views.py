"""
One endpoint, so the drill can prove the Feature is mounted and serving.

It reports the columns the database actually has rather than a version string,
because a version string is what the store was *told* it installed and the
columns are what it *has* — and the gap between those two is exactly what a
rollback drill exists to catch.
"""

from __future__ import annotations

from django.http import JsonResponse

from .models import DrillRecord


def index(request):
    return JsonResponse(
        {
            "records": list(DrillRecord.objects.values_list("reference", flat=True)),
            "columns": sorted(_columns()),
        }
    )


def _columns() -> list[str]:
    from django.db import connection

    with connection.cursor() as cursor:
        return [
            column.name
            for column in connection.introspection.get_table_description(cursor, "knight_drill_record")
        ]
