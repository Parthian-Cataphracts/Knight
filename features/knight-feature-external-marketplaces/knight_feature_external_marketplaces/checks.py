"""
The health check KNIGHT runs after installing this feature.

An install that finishes and then does not work is a failed install. This runs
the three things most likely to be broken after installing this one:

- **the queue query**, because it is what the flush worker runs on a timer, and a
  Feature whose queue query raises is one that silently delivers nothing;
- **the adapter registry**, because a connection naming an adapter that is not
  there is a queue that fills up and never drains;
- **the depth report**, which aggregates and is what an operator looks at first.

Nothing here contacts anybody. A health check that reached a partner would fail
whenever the partner was down, which is precisely when a store most needs to know
that its *own* installation is fine.
"""

from __future__ import annotations

import logging

logger = logging.getLogger(__name__)


def health() -> bool:
    """True when the queue can be read and the adapters are all present."""
    try:
        from . import adapters, services
        from .models import Connection, Message

        Connection.objects.exists()
        Message.objects.exists()

        services.queue_depth()
        services.connections()
        services.abandoned(limit=1)

        if adapters.LOOPBACK not in adapters.known():
            logger.error("The adapter registry is missing its own loopback adapter.")
            return False
    except Exception:  # noqa: BLE001 - any failure here means unhealthy
        logger.exception("External marketplaces health check failed.")
        return False

    return True
