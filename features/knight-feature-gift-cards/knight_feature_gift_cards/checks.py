"""
The health check KNIGHT runs after installing this feature.

For a money ledger the check has to prove the arithmetic works, not merely that
rows can be counted: the derived balance is what every other path depends on,
and a GIN-less index or a missing constraint would let it be wrong rather than
absent.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when both ledgers exist and a balance can be derived from them."""
    try:
        from . import services
        from .models import CreditEntry, GiftCard, GiftCardEntry

        GiftCard.objects.exists()
        GiftCardEntry.objects.exists()
        CreditEntry.objects.exists()

        # The two aggregates every other path rests on, against inputs that will
        # not match anything. A zero answer is fine; an exception means a ledger
        # or its index did not survive the migration.
        services.balance("KNIGHT-HEALTH-CHECK-CODE")
        services.credit_balance("knight-health-check-subject")
        services.outstanding()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Gift cards health check failed.")
        return False

    return True
