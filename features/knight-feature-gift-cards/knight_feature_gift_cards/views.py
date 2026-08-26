"""
Checking a gift card.

Read-only, and narrow on purpose. Issuing, redeeming, voiding and granting
credit all move money and are service calls the store's own checkout and admin
make — not routes anybody who can reach the store may POST to.

Even the read is careful. A balance lookup by code is an oracle for guessing
codes, so it answers the same shape for a card that does not exist as for one
that has no value left, and says nothing about who the card was bought for.
"""

from __future__ import annotations

from django.http import JsonResponse

from . import services


def check(request):
    """
    What a shopper is told about the code they typed.

    404 with a neutral message for an unknown code. Confirming that a code
    exists but is empty is still information an attacker enumerating codes can
    use, so the two are answered the same way.
    """
    held = services.balance(request.GET.get("code", ""))

    if held is None or held.remaining <= services.ZERO:
        return JsonResponse(
            {"detail": "No gift card with that code has any value on it."}, status=404
        )

    return JsonResponse(
        {
            "currency": held.currency,
            "remaining": str(held.remaining),
            "redeemable": held.redeemable,
            "expiresAt": held.expires_at.isoformat() if held.expires_at else None,
        }
    )


def credit(request, subject: str):
    """A customer's store credit balance."""
    return JsonResponse(
        {
            "subject": subject,
            "balance": str(services.credit_balance(subject)),
        }
    )
