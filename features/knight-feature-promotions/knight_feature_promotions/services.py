"""
The surface the base store calls, and the only one it may.

Everything crossing this boundary is plain data in both directions. The store
hands in basket lines it built from its own catalogue; this hands back a
description of which rule won and what it is worth. Neither side sees the
other's models, which is what makes this Feature genuinely removable.

The store decides what the answer means for an order — including turning it into
an order snapshot — because that is the store's table and its decision
([`feature-authoring.md`](../../../docs/feature-authoring.md)).
"""

from __future__ import annotations

from dataclasses import dataclass
from decimal import Decimal

from django.utils import timezone

from .models import Bundle, BuyXGetY, CampaignStatus


@dataclass(frozen=True)
class AdvancedOutcome:
    """
    Which advanced rule applied, and what it is worth.

    `stacks` travels with the outcome rather than being looked up separately,
    because the caller has to decide whether to add this to a coupon discount at
    exactly the moment it reads the amount.
    """

    rule_id: int
    rule_name: str
    rule_type: str
    discount_amount: Decimal
    stacks: bool


def price(subtotal: Decimal, *, lines=None, at=None) -> AdvancedOutcome | None:
    """
    The best advanced rule for this basket, or None when none applies.

    `subtotal` is accepted and deliberately unused for the arithmetic: every rule
    here prices from the lines, because "buy two of these" cannot be answered
    from a total. It stays in the signature because the cap belongs to the
    caller, which is where the base discount is also known.

    None rather than a zero outcome, so the caller can tell "no advanced rule
    applied" from "an advanced rule applied and came to nothing" without
    inspecting an amount.
    """
    moment = at or timezone.now()
    basket = list(lines or [])

    if not basket:
        return None

    best: AdvancedOutcome | None = None

    for rule_type, queryset in (
        ("BuyXGetY", BuyXGetY.objects.filter(status=CampaignStatus.ACTIVE)),
        ("Bundle", Bundle.objects.filter(status=CampaignStatus.ACTIVE).prefetch_related("items")),
    ):
        for rule in queryset.order_by("-priority", "id"):
            if not rule.is_live(moment):
                continue

            amount = rule.discount_for(basket)

            if amount <= Decimal("0"):
                continue

            if best is None or amount > best.discount_amount:
                best = AdvancedOutcome(
                    rule_id=rule.pk,
                    rule_name=rule.name,
                    rule_type=rule_type,
                    discount_amount=amount,
                    stacks=rule.stacks,
                )

    return best
