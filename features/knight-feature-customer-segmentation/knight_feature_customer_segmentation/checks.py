"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This one has
a second job the others do not: it is the only place that can catch a store where
the dependency resolved on paper and is wrong in practice.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the tables exist and the analytics dependency is actually usable."""
    try:
        from . import services
        from .models import Segment, SegmentMembership

        Segment.objects.exists()
        SegmentMembership.objects.exists()
        services.summary()

        # The important one. A store can have this Feature installed beside
        # analytics-core 1.0.x if anything ever bypasses the resolver, and every
        # segment would then compute to empty rather than fail - which reads as
        # a store with no customers.
        services._analytics()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Customer segmentation health check failed.")
        return False

    return True
