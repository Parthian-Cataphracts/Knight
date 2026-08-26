"""
Reading segments over HTTP.

Read-only. Recomputation is a job, not a request: it walks a window of events and
rewrites every membership row, which is not something a GET should do and not
something worth leaving open to whoever can reach the store.
"""

from __future__ import annotations

from django.http import JsonResponse

from . import services


def overview(request):
    """Every segment with its size."""
    return JsonResponse({"segments": services.summary()})


def members(request, slug: str):
    """Who is in one segment, highest value first."""
    return JsonResponse(
        {
            "segment": slug,
            "members": services.members_of(
                slug,
                limit=_int(request.GET.get("limit")) or 100,
                offset=_int(request.GET.get("offset")) or 0,
            ),
        }
    )


def for_subject(request, subject: str):
    """Which segments one customer is in."""
    return JsonResponse({"subject": subject, "segments": services.segments_for(subject)})


def _int(value) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None
