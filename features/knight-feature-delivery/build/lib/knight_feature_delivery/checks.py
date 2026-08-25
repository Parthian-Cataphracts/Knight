"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This asks
the database a real question rather than returning True.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when this feature's tables exist and are queryable."""
    try:
        from .models import DeliverySettings, DeliveryZone

        DeliveryZone.objects.exists()
        DeliverySettings.current()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Delivery health check failed.")
        return False

    return True
