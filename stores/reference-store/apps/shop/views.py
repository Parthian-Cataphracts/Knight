"""
The storefront.

This module is the demonstration of the rule that matters most in a store: the
business domain does not know KNIGHT exists. It imports one thing from the
integration layer — the feature façade — and asks it questions in the store's
own vocabulary. No client, no token, no HTTP, no idea where the answer comes
from (docs/store-integration.md §1).

It also demonstrates the distinction the whole product rests on. A capability is
servable only when the customer is entitled to it *and* the code that implements
it is installed. Entitled-but-not-installed is a delivery gap, and the store says
so rather than pretending the feature is off (docs/README.md rule 10).
"""

from __future__ import annotations

from django.http import HttpRequest, JsonResponse

from knight_integration.features import FeatureNotEntitled, is_available, is_enabled, is_installed, require

from .models import LoyaltyAccount, Product


def catalogue(request: HttpRequest) -> JsonResponse:
    """The shop itself. Always available: every store has a storefront."""
    products = Product.objects.filter(is_available=True).values("name", "slug", "price")

    return JsonResponse(
        {
            "products": [{**product, "price": str(product["price"])} for product in products],
            # Shown so the reference store is legible from the outside; a real
            # storefront would simply render or omit the feature.
            "capabilities": {
                "loyalty": _describe("loyalty"),
                "analytics": _describe("analytics"),
            },
        }
    )


def loyalty(request: HttpRequest) -> JsonResponse:
    """
    A paid capability, enforced server-side.

    The check is here, on the server, and not in whatever renders the page. A
    frontend flag is never sufficient (docs/store-integration.md §3).
    """
    try:
        require("loyalty")
    except FeatureNotEntitled as exc:
        return JsonResponse(
            {"detail": str(exc), "code": "feature_not_entitled"},
            status=402,
        )

    if not is_installed("loyalty"):
        # Paid for, not delivered. Answering 503 rather than 402 is the honest
        # distinction: the customer has bought this and it is KNIGHT's job to
        # install it, not theirs to buy it again.
        return JsonResponse(
            {
                "detail": "This store is entitled to loyalty, but the feature is not installed yet.",
                "code": "feature_not_installed",
            },
            status=503,
        )

    return JsonResponse(
        {"accounts": list(LoyaltyAccount.objects.values("email", "points"))}
    )


def boom(request: HttpRequest) -> JsonResponse:
    """
    Raises on purpose, so error reporting can be seen working end to end.

    Present in the reference store because "did the error reach KNIGHT" is the
    single most common question when wiring a new store up; a real store would
    not ship this.
    """
    raise RuntimeError("The reference store raised this deliberately.")


def _describe(slug: str) -> str:
    if is_available(slug):
        return "available"

    if is_enabled(slug):
        return "entitled, not installed"

    return "not entitled"
