"""
The location endpoints.

JSON only, and read-mostly. There is exactly one write, and it is the one act a
store's checkout genuinely performs: deciding where an order is handled. Every
other change here — describing a branch, entering opening hours, putting somebody
on a rota — is administration a merchant does in a dashboard against KNIGHT, not
something that should be postable to a store.

`place` deliberately answers for a code nobody has described, with
`"described": false` and a 404. That is not an error state: on the day this
Feature is installed every code in the store is in it.
"""

from __future__ import annotations

import json

from django.http import JsonResponse
from django.views.decorators.http import require_http_methods

from . import services


def places(request):
    """Every branch a merchant has described."""
    return JsonResponse(
        {
            "locations": [
                _place(place, open_now=services.is_open(place.code))
                for place in services.places(
                    active_only=request.GET.get("all") != "1",
                    kind=request.GET.get("kind", ""),
                )
            ]
        }
    )


def place(request, code: str):
    """One branch, and whether it is trading right now."""
    found = services.describe(code)

    if found is None:
        # A code in use that nobody has named. The store is not broken and
        # neither is the caller; there is simply nothing to say about it yet.
        return JsonResponse({"code": code, "described": False}, status=404)

    return JsonResponse({"described": True, **_place(found, open_now=services.is_open(found.code))})


def roster(request, code: str):
    """Who is assigned to a branch today."""
    try:
        staff = services.roster(code)
    except services.UnknownLocation as exc:
        return JsonResponse({"error": str(exc)}, status=404)

    return JsonResponse(
        {
            "location": code.upper(),
            "staff": [
                {"code": member.code, "name": member.name, "phone": member.phone}
                for member in staff
            ],
        }
    )


def menu(request, code: str):
    """
    What this branch does not sell.

    The exceptions, not the menu, because that is what the table holds. A caller
    that wanted the menu takes the store's own catalogue and removes these, which
    is the only reading that stays correct when a product is added.
    """
    try:
        services.describe(code)
        missing = services.unavailable_at(code)
    except services.UnknownLocation as exc:
        return JsonResponse({"error": str(exc)}, status=404)

    return JsonResponse({"location": code.upper(), "unavailable": missing})


def routing(request, number: int):
    """Where one order went."""
    decision = services.routing_for(number)

    if decision is None:
        return JsonResponse({"orderNumber": number, "routed": False}, status=404)

    return JsonResponse({"routed": True, **_decision(decision)})


@require_http_methods(["POST"])
def route(request):
    """
    Decides where an order is handled.

    Idempotent by construction: asked twice about the same order it returns the
    decision that was already made, because where an order was handled is a fact
    about that order rather than a function of today's rules.
    """
    payload = _body(request)

    try:
        decision = services.route(
            payload.get("orderNumber", 0),
            postal_code=payload.get("postalCode", ""),
            city=payload.get("city", ""),
            zone=payload.get("zone", ""),
            prefer=payload.get("prefer", ""),
        )
    except services.NowhereToRouteTo as exc:
        # 409 rather than 400: the request was well formed and the world was not
        # in the state it assumed. A checkout needs to tell those apart to say
        # "we are closed" rather than "something went wrong".
        return JsonResponse({"error": str(exc)}, status=409)
    except services.LocationError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    return JsonResponse(_decision(decision))


def _place(place, *, open_now: bool) -> dict:
    return {
        "code": place.code,
        "name": place.name,
        "kind": place.kind,
        "timezone": place.timezone,
        "city": place.city,
        "postalCode": place.postal_code,
        "isDefault": place.is_default,
        "isActive": place.is_active,
        "takesCustomers": place.takes_customers,
        "isOpenNow": open_now,
    }


def _decision(decision) -> dict:
    return {
        "orderNumber": decision.order_number,
        "location": decision.location,
        "reason": decision.reason,
        "decidedAt": decision.decided_at.isoformat(),
    }


def _body(request) -> dict:
    try:
        return json.loads(request.body or b"{}")
    except (ValueError, TypeError):
        return {}
