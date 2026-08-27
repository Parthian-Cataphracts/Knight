"""
The restaurant endpoints.

JSON only. The reads are the three screens a restaurant actually runs — the
kitchen board, the floor plan, and the times a checkout may offer — and the
writes are the four things a member of staff does with a finger: seating a party,
clearing a table, bumping a ticket, and taking a slot.

Opening a ticket is deliberately not here. A ticket exists because an order was
placed, and the store's own checkout is what knows that happened; an endpoint
that let anything post a ticket would be a kitchen screen anybody could fill up.
"""

from __future__ import annotations

import json

from django.http import JsonResponse
from django.utils import timezone
from django.utils.dateparse import parse_datetime
from django.views.decorators.http import require_http_methods

from . import services
from .models import DEFAULT_LOCATION, ServiceStyle


def board(request):
    """
    What the kitchen still has to make, oldest first.

    Filterable by station, because the grill chef does not want to read the
    drinks and a screen showing everything is a screen nobody reads.
    """
    location = request.GET.get("location", DEFAULT_LOCATION)
    station = request.GET.get("station", "")

    return JsonResponse(
        {
            "location": location,
            "station": station,
            "tickets": [_ticket(ticket) for ticket in services.board(location=location, station=station)],
        }
    )


def ticket(request, number: int):
    """One ticket and everywhere it has been."""
    try:
        found = services.ticket(number)
    except services.UnknownTicket:
        return JsonResponse({"number": number, "found": False}, status=404)

    return JsonResponse(
        {
            "found": True,
            **_ticket(found),
            "events": [
                {
                    "from": event.from_state,
                    "to": event.to_state,
                    "actor": event.actor,
                    "note": event.note,
                    "occurredAt": event.occurred_at.isoformat(),
                }
                for event in found.events.all()
            ],
        }
    )


def floor(request):
    """Every table and what is happening at it."""
    location = request.GET.get("location", DEFAULT_LOCATION)

    return JsonResponse(
        {
            "location": location,
            "tables": [
                {
                    "code": status.code,
                    "name": status.name,
                    "area": status.area,
                    "seats": status.seats,
                    "isSeated": status.is_seated,
                    "partySize": status.party_size,
                    "label": status.label,
                    "seatedMinutes": status.seated_minutes,
                    "openTickets": status.open_tickets,
                }
                for status in services.floor(location=location)
            ],
        }
    )


def load(request):
    """
    How busy the kitchen is, and what that adds to a new order.

    The number behind every promise, exposed on its own because "why did it say
    forty minutes" is the first question a manager asks and the hardest one to
    answer from a promised time alone.
    """
    location = request.GET.get("location", DEFAULT_LOCATION)
    current = services.load(location=location)

    return JsonResponse(
        {
            "location": current.location,
            "liveTickets": current.live_tickets,
            "outstandingUnits": current.outstanding_units,
            "throughputUnitsPerHour": current.throughput_units_per_hour,
            "backlogMinutes": current.backlog_minutes,
        }
    )


def slots(request):
    """
    The times a checkout may offer.

    Only times that can actually be taken. A time shown and then refused at
    payment is the checkout equivalent of a menu with nothing behind it.
    """
    offers = services.offers(
        units=_int(request.GET.get("units")) or 1,
        service=request.GET.get("service", ServiceStyle.COLLECTION),
        location=request.GET.get("location", DEFAULT_LOCATION),
        within_hours=_int(request.GET.get("hours")),
    )

    return JsonResponse(
        {
            "slots": [
                {
                    "startsAt": offer.starts_at.isoformat(),
                    "minutes": offer.minutes,
                    "service": offer.service,
                    "location": offer.location,
                    "remainingUnits": offer.remaining_units,
                }
                for offer in offers
            ]
        }
    )


@require_http_methods(["POST"])
def seat(request):
    """Sits a party at a table."""
    payload = _body(request)

    try:
        session = services.seat(
            payload.get("table", ""),
            party_size=payload.get("partySize", 1),
            label=payload.get("label", ""),
        )
    except services.UnknownTable as exc:
        return JsonResponse({"error": str(exc)}, status=404)
    except services.TableInUse as exc:
        # 409 rather than 400: the request was well formed and the world was not
        # in the state it assumed, which is exactly what a till needs to tell
        # apart to offer the open session instead.
        return JsonResponse({"error": str(exc)}, status=409)
    except services.RestaurantError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    return JsonResponse(
        {
            "table": session.table.code,
            "partySize": session.party_size,
            "label": session.label,
            "openedAt": session.opened_at.isoformat(),
        }
    )


@require_http_methods(["POST"])
def clear(request):
    """Closes the open session at a table."""
    payload = _body(request)

    try:
        session = services.clear(payload.get("table", ""))
    except services.UnknownTable as exc:
        return JsonResponse({"error": str(exc)}, status=404)

    if session is None:
        # Not an error. Two members of staff both tidying up is how a restaurant
        # works, and the second one has not done anything wrong.
        return JsonResponse({"table": payload.get("table", ""), "cleared": False})

    return JsonResponse(
        {
            "table": session.table.code,
            "cleared": True,
            "closedAt": session.closed_at.isoformat(),
        }
    )


@require_http_methods(["POST"])
def advance(request, number: int):
    """Bumps a ticket to its next state."""
    payload = _body(request)

    try:
        moved = services.advance(
            number,
            payload.get("state", ""),
            actor=payload.get("actor", ""),
            note=payload.get("note", ""),
        )
    except services.UnknownTicket as exc:
        return JsonResponse({"error": str(exc)}, status=404)
    except services.InvalidTransition as exc:
        return JsonResponse({"error": str(exc)}, status=409)
    except services.RestaurantError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    return JsonResponse(_ticket(moved))


@require_http_methods(["POST"])
def book(request):
    """
    Takes part of a slot for one basket.

    The write a checkout makes while somebody is choosing a time, and the reason
    the whole capacity model exists: a time offered and not taken is a time two
    people are given.
    """
    payload = _body(request)
    starts_at = parse_datetime(str(payload.get("startsAt", "")) or "")

    if starts_at is None:
        return JsonResponse({"error": "startsAt has to be a timestamp."}, status=400)

    if timezone.is_naive(starts_at):
        starts_at = timezone.make_aware(starts_at)

    try:
        booking = services.book(
            starts_at,
            reference=payload.get("reference", ""),
            units=payload.get("units", 1),
            service=payload.get("service", ServiceStyle.COLLECTION),
            location=payload.get("location", DEFAULT_LOCATION),
        )
    except services.UnknownSlot as exc:
        return JsonResponse({"error": str(exc)}, status=404)
    except services.NoCapacity as exc:
        return JsonResponse(
            {"error": str(exc), "remainingUnits": exc.remaining},
            status=409,
        )
    except services.RestaurantError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    return JsonResponse(
        {
            "reference": booking.reference,
            "startsAt": booking.slot.starts_at.isoformat(),
            "units": booking.units,
            "state": booking.state,
            "expiresAt": booking.expires_at.isoformat(),
        }
    )


def _ticket(ticket) -> dict:
    return {
        "number": ticket.number,
        "orderNumber": ticket.source_order_number,
        "service": ticket.service,
        "state": ticket.state,
        "location": ticket.location,
        "table": ticket.session.table.code if ticket.session_id else "",
        "promisedAt": ticket.promised_at.isoformat() if ticket.promised_at else None,
        "startAfter": ticket.start_after.isoformat() if ticket.start_after else None,
        "note": ticket.note,
        "lines": [
            {
                "id": line.pk,
                "sku": line.sku,
                "name": line.name,
                "quantity": line.quantity,
                "station": line.station.code if line.station_id else "",
                "prepMinutes": line.prep_minutes,
                "modifications": line.modifications,
                "state": line.state,
            }
            for line in ticket.lines.all()
        ],
    }


def _body(request) -> dict:
    try:
        return json.loads(request.body or b"{}")
    except (ValueError, TypeError):
        return {}


def _int(value) -> int | None:
    try:
        return int(value)
    except (TypeError, ValueError):
        return None
