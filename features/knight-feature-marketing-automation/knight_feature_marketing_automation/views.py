"""
Reading what the campaigns did, and letting somebody unsubscribe.

Almost everything here is read-only. The exception is the unsubscribe route, and
it is a POST that takes no authentication on purpose: an unsubscribe link that
requires a login is an unsubscribe link that does not work, which is both a
complaint and, in most of the markets this sells into, unlawful.
"""

from __future__ import annotations

import json

from django.http import HttpResponseNotAllowed, JsonResponse
from django.views.decorators.csrf import csrf_exempt

from . import config, services


def overview(request):
    """Every campaign and what it has sent."""
    return JsonResponse({"campaigns": services.summary()})


def history(request, slug: str):
    """One campaign's send log."""
    return JsonResponse({"campaign": slug, "sends": services.history(slug)})


def configuration(request):
    """
    What this Feature is configured to do, with no secret values in it.

    Secrets are reported as names and a boolean. A support conversation needs to
    know whether the key arrived, never what it is.
    """
    return JsonResponse(config.describe())


@csrf_exempt
def unsubscribe(request):
    """
    Adds an address to the suppression list.

    `csrf_exempt` because this is reached from a link in an email, where there is
    no session and no token to carry. Said out loud because an exemption nobody
    explained is an exemption nobody can review.

    Answers the same way whether or not the address was known. Confirming which
    addresses a shop holds would turn an unsubscribe endpoint into a way of
    checking whether somebody is a customer.
    """
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])

    try:
        payload = json.loads(request.body or b"{}")
    except json.JSONDecodeError:
        return JsonResponse({"error": "The request body is not valid JSON."}, status=400)

    email = str(payload.get("email", "")).strip()

    if not email:
        return JsonResponse({"error": "An email address is required."}, status=400)

    services.suppress(email, detail="Unsubscribed from an email link")

    return JsonResponse({"detail": "That address will not be mailed again."})
