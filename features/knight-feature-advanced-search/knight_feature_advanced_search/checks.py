"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This runs a
real query through the real index rather than returning True: the thing most
likely to be wrong after installing a search feature is the GIN index or the
tsvector column, and neither shows up in an import.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the index exists and a query can actually be run against it."""
    try:
        from . import services
        from .models import SearchDocument

        SearchDocument.objects.exists()

        # Exercises the tsvector column and the ranking expression. A query that
        # matches nothing is fine; one that raises means the column or the index
        # did not survive the migration.
        services.search("knight-health-check-query-that-matches-nothing")
        services.stats()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Advanced search health check failed.")
        return False

    return True
