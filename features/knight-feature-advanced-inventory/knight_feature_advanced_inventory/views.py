"""
The inventory endpoints.

JSON only, and read-mostly. Stock is changed by the store's own code calling
`services` at the point where the thing actually happened — a payment taken, a
delivery unpacked — not by somebody posting to an endpoint, which is why the two
write endpoints here are the two acts a member of staff genuinely performs at a
screen: counting a shelf and receiving a delivery.

Everything a shopper could reach is a read. `available` is the only one a
storefront needs, and it deliberately returns one number.
"""

from __future__ import annotations

import json

from django.http import JsonResponse
from django.views.decorators.http import require_http_methods

from . import services
from .models import DEFAULT_LOCATION


def levels(request):
    """Every tracked item's numbers, for a stock screen."""
    location = request.GET.get("location", DEFAULT_LOCATION)

    return JsonResponse(
        {
            "location": location,
            "items": [_level(entry) for entry in services.levels(location=location)],
        }
    )


def availability(request, sku: str):
    """
    What may be sold of one thing.

    The endpoint a product page calls. On hand and held are included because a
    merchant looking at the same page needs to know which of the two is keeping
    the number down.
    """
    location = request.GET.get("location", DEFAULT_LOCATION)

    try:
        entry = services.level(sku, location=location)
    except services.UnknownItem:
        # Not an error: a store asking about something it does not track is
        # asking a reasonable question, and the answer is that it is not tracked.
        return JsonResponse({"sku": sku, "tracked": False}, status=404)

    return JsonResponse({"tracked": True, **_level(entry)})


def history(request, sku: str):
    """The recent movements of one item: the answer to 'why is this 7'."""
    try:
        movements = services.history(sku, limit=_int(request.GET.get("limit")) or 50)
    except services.UnknownItem:
        return JsonResponse({"sku": sku, "tracked": False}, status=404)

    return JsonResponse(
        {
            "sku": sku,
            "movements": [
                {
                    "quantity": str(movement.quantity),
                    "reason": movement.reason,
                    "reference": movement.reference,
                    "note": movement.note,
                    "location": movement.location,
                    "occurredAt": movement.occurred_at.isoformat(),
                }
                for movement in movements
            ],
        }
    )


def alerts(request):
    """What is low right now."""
    return JsonResponse(
        {
            "alerts": [
                {
                    "sku": alert.item.sku,
                    "name": alert.item.name,
                    "kind": alert.kind,
                    "location": alert.location,
                    "available": str(alert.available),
                    "threshold": str(alert.threshold),
                    "raisedAt": alert.raised_at.isoformat(),
                }
                for alert in services.open_alerts()
            ]
        }
    )


def reorder(request):
    """What to buy, and what is already on its way."""
    location = request.GET.get("location", DEFAULT_LOCATION)

    return JsonResponse(
        {
            "location": location,
            "suggestions": [
                {
                    "sku": suggestion.sku,
                    "name": suggestion.name,
                    "available": str(suggestion.available),
                    "reorderPoint": str(suggestion.reorder_point),
                    "suggestedQuantity": str(suggestion.suggested_quantity),
                    "hasQuantity": suggestion.has_a_quantity,
                    "supplier": suggestion.supplier_code,
                    "onOrder": str(suggestion.on_order),
                }
                for suggestion in services.reorder_suggestions(location=location)
            ],
        }
    )


def purchase_orders(request):
    """Orders placed and not yet fully arrived."""
    return JsonResponse(
        {
            "orders": [
                {
                    "reference": order.reference,
                    "supplier": order.supplier.code,
                    "state": order.state,
                    "expectedAt": order.expected_at.isoformat() if order.expected_at else None,
                    "lines": [
                        {
                            "sku": line.item.sku,
                            "ordered": str(line.quantity_ordered),
                            "received": str(line.quantity_received),
                            "outstanding": str(line.outstanding),
                        }
                        for line in order.lines.select_related("item")
                    ],
                }
                for order in services.outstanding_orders()
            ]
        }
    )


def search(request):
    """
    Items matching what somebody typed, for a stock picker.

    Typo-tolerant, because the person typing has a delivery in their hands and
    half a product name in their head.
    """
    found = services.find_items(request.GET.get("q", ""))

    return JsonResponse(
        {
            "results": [
                {"sku": item.sku, "name": item.name, "unit": item.unit}
                for item in found
            ]
        }
    )


@require_http_methods(["POST"])
def stocktake(request):
    """
    Records what somebody actually found on the shelf.

    A write, and one of only two here, because it is a thing a person does at a
    screen with a clipboard in their other hand.
    """
    payload = _body(request)
    sku = payload.get("sku", "")

    try:
        movement = services.count(
            sku,
            payload.get("counted", 0),
            location=payload.get("location", DEFAULT_LOCATION),
            note=payload.get("note", ""),
        )
    except services.UnknownItem:
        return JsonResponse({"error": f"No stock item has the SKU '{sku}'."}, status=404)
    except services.InventoryError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    if movement is None:
        # The count agreed with the books. Reported as its own outcome rather
        # than as a movement of zero, which is both a lie and refused by the
        # database.
        return JsonResponse({"sku": sku, "corrected": False, "difference": "0.000"})

    return JsonResponse({"sku": sku, "corrected": True, "difference": str(movement.quantity)})


@require_http_methods(["POST"])
def receive(request):
    """Records part or all of a purchase-order line arriving."""
    payload = _body(request)

    try:
        movement = services.receive_line(
            payload.get("reference", ""),
            payload.get("sku", ""),
            payload.get("quantity", 0),
        )
    except services.UnknownItem as exc:
        return JsonResponse({"error": str(exc)}, status=404)
    except services.InventoryError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    return JsonResponse(
        {
            "reference": movement.reference,
            "sku": movement.item.sku,
            "received": str(movement.quantity),
        }
    )


def _level(entry) -> dict:
    return {
        "sku": entry.sku,
        "name": entry.name,
        "location": entry.location,
        "onHand": str(entry.on_hand),
        "held": str(entry.held),
        "available": str(entry.available),
        "reorderPoint": str(entry.reorder_point),
        "isLow": entry.is_low,
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


