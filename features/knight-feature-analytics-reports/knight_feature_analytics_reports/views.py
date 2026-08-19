"""
The reporting endpoint.

Reads through analytics-core's service functions, never its models. The import is
deferred into the view rather than done at module import so that a store which
has this feature installed while analytics-core is mid-upgrade answers one
request badly instead of failing to start.
"""

from __future__ import annotations

from datetime import date, timedelta

from django.http import JsonResponse


def daily_summary(request):
    """Event counts per day for a short trailing window."""
    from knight_feature_analytics_core import services

    try:
        window = int(request.GET.get("days", 7))
    except (TypeError, ValueError):
        window = 7

    # Bounded rather than trusted: an unbounded window is one query per day for
    # as many days as the caller asks for.
    window = max(1, min(window, 90))

    today = date.today()
    summary = {}

    for offset in range(window):
        day = today - timedelta(days=offset)
        counts = services.counts_for(day)

        if counts:
            summary[day.isoformat()] = counts

    return JsonResponse({"windowDays": window, "days": summary})
