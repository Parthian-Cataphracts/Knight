"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This runs
the three things most likely to be broken after installing this one, rather than
returning True:

- **the board query**, because it is what every screen in the kitchen calls every
  few seconds, and a Feature whose board raises is a Feature with no usable
  surface at all;
- **the promise arithmetic**, because a promised time is derived from an
  aggregate over the live tickets, and an aggregate that raises makes every new
  order a 500;
- **the slot arithmetic**, because throttling is what a restaurant actually buys
  this for, and a store where `offers()` raises would take orders it has no
  capacity to cook.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the room, the kitchen and the diary can all be read."""
    try:
        from . import services
        from .models import KitchenTicket, Table

        Table.objects.exists()
        KitchenTicket.objects.exists()

        # Each of these exercises a join and an aggregate without needing any
        # data: a restaurant that has not opened yet must still pass this.
        services.floor()
        services.load()
        services.promise([])
        services.offers()
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Restaurant operations health check failed.")
        return False

    return True
