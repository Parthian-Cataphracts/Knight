"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This runs
the two things most likely to be broken after installing this one, rather than
returning True:

- **reading a location that has not been described**, because that is the state
  every code in the store is in at the moment of the install, and a `describe()`
  that raised rather than returning None would break every caller on day one;
- **the routing decision**, because it joins four tables and consults opening
  hours in a timezone, and a store where `route()` raises is a store that cannot
  take an order at all.

The routing check is a read of an order number nothing will ever use, so it
decides nothing and writes nothing. A health check that created a routing
decision would be a health check that changed the thing it was measuring.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when an undescribed code reads cleanly and a route can be looked up."""
    try:
        from . import services
        from .models import Location, OrderRouting

        Location.objects.exists()
        OrderRouting.objects.exists()

        # Each of these exercises a join without needing any data: a merchant who
        # has described nothing yet must still pass this.
        services.places()
        services.default_place()
        services.describe("knight-health-check-code-that-does-not-exist")
        services.routing_for(0)
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("Multi-location health check failed.")
        return False

    return True
