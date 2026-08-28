"""Liveness, and nothing else."""

from __future__ import annotations

from django.db import connection
from django.http import JsonResponse


def healthz(request):
    """
    Whether this service can do its job.

    The database is checked because everything this service does needs it, and
    a process that answers 200 while its database is unreachable is a health
    check that reports the wrong thing at exactly the moment it matters.

    Deliberately unauthenticated: a probe that needed a credential could not be
    run by a load balancer, and this reveals nothing beyond up or down.
    """
    try:
        with connection.cursor() as cursor:
            cursor.execute("SELECT 1")
    except Exception as exc:  # noqa: BLE001 - any failure here means unhealthy
        return JsonResponse({"status": "unhealthy", "detail": str(exc)[:200]}, status=503)

    return JsonResponse({"status": "healthy", "service": "subscriptions"})
