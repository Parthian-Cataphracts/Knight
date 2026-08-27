"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This runs
the three things most likely to be broken after installing this one:

- **the due query**, because it is what the billing worker runs on a timer, and a
  Feature whose due query raises is one that silently charges nobody;
- **the summary arithmetic**, because it aggregates over the periods and is what
  every screen and every report reads;
- **the provider**, because a store that installed this and configured no
  provider must find that out here rather than a month later when a merchant
  notices nobody has been charged.

The provider check asks for a charge of nothing against a reference that does not
exist, which every provider refuses. It moves no money by construction — a health
check that could charge somebody would be worse than no health check.
"""

from __future__ import annotations

import logging
from decimal import Decimal

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the billing path can be walked without money moving."""
    try:
        from . import providers, services
        from .models import BillingPeriod, Subscription

        Subscription.objects.exists()
        BillingPeriod.objects.exists()

        # Exercises the index the worker uses, without needing any data: a store
        # with no subscriptions must still pass this.
        services.due().count()

        result = providers.charge(
            provider="none",
            amount=Decimal("0.00"),
            currency="XTS",
            reference="knight-health-check",
        )

        # The `none` provider must refuse. If this ever returns success, a
        # misconfigured store is being told its payments work.
        if result.outcome != "refused":
            logger.error("The 'none' provider reported %s rather than refusing.", result.outcome)
            return False
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Subscriptions health check failed.")
        return False

    return True
