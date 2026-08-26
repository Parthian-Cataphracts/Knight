"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. For a
ledger feature the check has to do more than count rows: it has to prove the
balance can actually be derived, because that aggregate is the one thing every
other path depends on.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the ledger exists and a balance can be computed from it."""
    try:
        from . import services
        from .models import Account, Programme, Tier, Transaction

        Account.objects.exists()
        Transaction.objects.exists()
        Tier.objects.exists()
        Programme.current()

        # The aggregate every other path rests on, against a subject that will
        # not exist. A zero answer is fine; an exception means the ledger or its
        # index did not survive the migration.
        services.balance_of("knight-health-check-subject")
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Loyalty rewards health check failed.")
        return False

    return True
