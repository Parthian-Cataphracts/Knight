"""
The routes this feature contributes.

Two surfaces on purpose. The HTML page is what makes the feature *visible* — a
Feature whose only evidence of working is a row in a table cannot be verified by
looking at it, and this phase exists to verify delivery by looking. The JSON
endpoints are what a real storefront would call.

Everything is read-mostly and unauthenticated except the moderation routes,
which are not exposed here at all: moderation belongs to whoever runs the store
and this feature has no way to know who that is. It is reachable through
`services.publish()` and `services.reject()`, which a store's own admin calls.
"""

from __future__ import annotations

import json

from django.http import HttpResponseNotAllowed, JsonResponse
from django.shortcuts import render
from django.views.decorators.csrf import csrf_exempt

from . import services


def product_reviews(request, product_id: int):
    """
    The reviews for one product, as a page.

    Renders a template shipped inside this package and loads a stylesheet from
    its static directory. Both are here because a delivered package's templates
    and static files are a delivery path of their own: an installer that copies
    the Python and misses the assets produces a feature that imports fine and
    renders nothing.
    """
    summary = services.summary_for(product_id)
    reviews = services.published_for(product_id, limit=_limit(request))

    return render(
        request,
        "reviews_ratings/product_reviews.html",
        {
            "product_id": product_id,
            "summary": summary,
            "reviews": reviews,
            # Bars for the distribution, worked out here rather than in the
            # template: arithmetic in a template is arithmetic nobody can test.
            "bars": _bars(summary),
        },
    )


def product_reviews_json(request, product_id: int):
    """The same thing a storefront would fetch."""
    summary = services.summary_for(product_id)
    reviews = services.published_for(product_id, limit=_limit(request))

    return JsonResponse(
        {
            "productId": product_id,
            "count": summary.count,
            "average": None if summary.average is None else str(summary.average),
            "distribution": summary.distribution,
            "reviews": [
                {
                    "id": review.id,
                    "author": review.author_name,
                    "rating": review.rating,
                    "title": review.title,
                    "body": review.body,
                    "verifiedPurchase": review.is_verified_purchase,
                    "publishedAt": review.published_at.isoformat() if review.published_at else None,
                    "reply": review.reply,
                }
                for review in reviews
            ],
        }
    )


@csrf_exempt
def submit_review(request, product_id: int):
    """
    Accepts a review and returns it as pending.

    `csrf_exempt` because this route is a machine-to-machine surface for a
    storefront that has its own session handling; the store's own form would
    call `services.submit()` directly and keep its CSRF protection. Said out loud
    because an exemption nobody explained is an exemption nobody can review.
    """
    if request.method != "POST":
        return HttpResponseNotAllowed(["POST"])

    try:
        payload = json.loads(request.body or b"{}")
    except json.JSONDecodeError:
        return JsonResponse({"error": "The request body is not valid JSON."}, status=400)

    try:
        review = services.submit(
            product_id,
            rating=int(payload.get("rating", 0)),
            author_name=str(payload.get("author", "")),
            title=str(payload.get("title", "")),
            body=str(payload.get("body", "")),
            shopper_id=payload.get("shopperId"),
        )
    except (TypeError, ValueError):
        return JsonResponse({"error": "A rating between 1 and 5 is required."}, status=400)
    except Exception as error:  # noqa: BLE001 - validation comes back as ValidationError
        return JsonResponse({"error": _first_message(error)}, status=400)

    # 202, not 201: it exists but nobody can see it yet, and a storefront that
    # showed it immediately would be showing unmoderated text.
    return JsonResponse({"id": review.pk, "status": review.status}, status=202)


def _limit(request) -> int:
    try:
        return max(1, min(int(request.GET.get("limit", 20)), 100))
    except (TypeError, ValueError):
        return 20


def _bars(summary) -> list[dict[str, object]]:
    """Star rows with a width percentage, highest star first."""
    if not summary.has_reviews:
        return []

    return [
        {
            "stars": stars,
            "count": summary.distribution.get(stars, 0),
            "percent": round(summary.distribution.get(stars, 0) * 100 / summary.count),
        }
        for stars in range(5, 0, -1)
    ]


def _first_message(error: Exception) -> str:
    """The first human-readable line out of a Django ValidationError."""
    messages = getattr(error, "messages", None)

    if messages:
        return str(messages[0])

    return str(error)
