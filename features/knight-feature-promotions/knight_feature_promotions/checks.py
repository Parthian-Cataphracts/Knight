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
        from .models import Bundle, BundleItem, BuyXGetY

        BuyXGetY.objects.exists()
        Bundle.objects.exists()
        BundleItem.objects.exists()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Advanced promotions health check failed.")
        return False

    return True
