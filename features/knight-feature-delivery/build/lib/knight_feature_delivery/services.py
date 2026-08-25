"""
Quoting a delivery fee.

The only entry point the base store uses, and a function rather than a model for
the same reason as promotions: the store has to keep working when this Feature is
not installed, so every call site must be able to fall back.
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal

from .models import DeliverySettings, DeliveryZone


@dataclass(frozen=True)
class DeliveryQuote:
    """
    What delivery would cost, in terms the base store can snapshot into
    `orders.OrderFulfillment`.
    """

    zone_id: int | None
    zone_name: str
    fee: Decimal
    accepted: bool
    reason: str = ""


def quote(zone_id: int, subtotal: Decimal) -> DeliveryQuote:
    """
    Prices delivery to one zone.

    A refusal carries its reason rather than being a bare False, because the
    shopper is about to be told why — and "we do not deliver there" and "your
    basket is too small" lead to completely different next actions.
    """
    settings = DeliverySettings.current()
    zone = DeliveryZone.objects.filter(pk=zone_id).first()

    if zone is None:
        return DeliveryQuote(None, "", Decimal("0"), False, "That delivery area is not available.")

    if not settings.is_accepting_orders:
        return DeliveryQuote(
            zone.pk, zone.name, zone.fee, False, "The store has paused deliveries."
        )

    if not zone.accepts(subtotal, settings):
        minimum = zone.minimum_for(settings)

        return DeliveryQuote(
            zone.pk,
            zone.name,
            zone.fee,
            False,
            f"Deliveries to {zone.name} start at {minimum}." if minimum else "That area is unavailable.",
        )

    return DeliveryQuote(zone.pk, zone.name, zone.fee, True)
