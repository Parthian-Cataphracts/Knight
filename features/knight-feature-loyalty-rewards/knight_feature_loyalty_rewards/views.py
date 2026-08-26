"""
Reading a loyalty balance.

Read-only over HTTP. Earning and redeeming move money-adjacent state and are
service calls the store's checkout makes, not routes anybody who can reach the
store may POST to.
"""

from __future__ import annotations

from django.http import JsonResponse

from . import services
from .models import Programme, Tier


def balance(request, subject: str):
    """What one customer has, and what is about to expire."""
    held = services.balance_of(subject)

    return JsonResponse(
        {
            "subject": held.subject,
            "points": held.points,
            "value": str(held.value),
            "lifetimePoints": held.lifetime_points,
            "tier": held.tier_name,
            "expiringSoon": held.expiring_soon,
        }
    )


def history(request, subject: str):
    """The ledger as a customer would read it."""
    return JsonResponse({"subject": subject, "history": services.history(subject)})


def programme(request):
    """The rules in force, so a storefront can explain them without hard-coding."""
    current = Programme.current()
    tiers = Tier.objects.all()

    return JsonResponse(
        {
            "active": current.is_active,
            "pointsPerCurrencyUnit": str(current.points_per_currency_unit),
            "currencyPerPoint": str(current.currency_per_point),
            "expiryMonths": current.expiry_months,
            "minimumRedemptionPoints": current.minimum_redemption_points,
            "tiers": [
                {
                    "name": tier.name,
                    "slug": tier.slug,
                    "thresholdPoints": tier.threshold_points,
                    "earnMultiplier": str(tier.earn_multiplier),
                    "benefits": tier.benefits,
                }
                for tier in tiers
            ],
        }
    )
