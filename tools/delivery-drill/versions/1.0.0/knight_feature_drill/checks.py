"""
The health check KNIGHT runs after installing this.

It reads the table, which is the only thing here that can be broken: the package
is trivial, so a failure means the migration did not apply and the install must
be reported failed rather than healthy.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the drill's table exists and can be read."""
    try:
        from .models import DrillRecord

        DrillRecord.objects.exists()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Drill health check failed.")
        return False

    return True
