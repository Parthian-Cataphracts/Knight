"""
The health check for this feature.

It checks the dependency, not itself. This feature has no tables and no state;
the only way it can be broken is if the feature it reads from is absent or
unusable, so that is the question worth asking.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when analytics-core is importable and answering."""
    try:
        from knight_feature_analytics_core import services
        from datetime import date

        services.counts_for(date.today())
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Analytics Reports health check failed; its dependency is not usable.")
        return False

    return True
