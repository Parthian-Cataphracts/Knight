"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This is what
tells the difference, so it asks the database a real question instead of
returning True.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when this feature's tables exist and are queryable."""
    try:
        from .models import AnalyticsEvent

        AnalyticsEvent.objects.exists()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Analytics Core health check failed.")
        return False

    return True
