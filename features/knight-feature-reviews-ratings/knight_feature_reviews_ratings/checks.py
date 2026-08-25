"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This asks
the database a real question, and renders the template, rather than returning
True: a package whose assets did not arrive imports perfectly well and then
fails on the first product page somebody opens.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when this feature's tables exist and its template can be rendered."""
    try:
        from django.template.loader import get_template

        from .models import Review, ReviewReply
        from .services import summary_for

        Review.objects.exists()
        ReviewReply.objects.exists()

        # Exercises the aggregate the product page depends on, against a product
        # id that will not exist. A summary that cannot be computed is a broken
        # install even when the table is there.
        summary_for(0)

        get_template("reviews_ratings/product_reviews.html")
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Reviews and ratings health check failed.")
        return False

    return True
