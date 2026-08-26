"""
Reading the reports and the spend.

Read-only. Generating a report can cost money, so it is a worker and a service
call, not a route anybody who can reach the store may hit — an endpoint that
spends on each request is a way to run up somebody's bill.
"""

from __future__ import annotations

from django.http import JsonResponse

from . import config, services


def latest(request):
    """The most recent report."""
    report = services.latest()

    if report is None:
        # A store that has never generated one is not an error. Saying so beats
        # an empty object a caller has to guess about.
        return JsonResponse({"detail": "No report has been generated yet."}, status=404)

    return JsonResponse(report)


def history(request):
    """Recent reports, without their findings."""
    return JsonResponse({"reports": services.history()})


def usage(request):
    """
    What narration has cost this month, against the cap.

    Exposed because a merchant paying for this should be able to see what they
    are spending without asking anybody.
    """
    return JsonResponse(services.usage())


def configuration(request):
    """What this Feature is configured to do, with no secret values in it."""
    return JsonResponse(config.describe())
