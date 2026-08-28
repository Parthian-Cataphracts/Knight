"""
Forwarding this store's events to the Features that asked for them.

The store's own event bus, narrowed to the part external Features see. Nothing
here is a general-purpose message broker and it should not become one: what it
does is look up who subscribed to an event and hand each delivery to the queue.

Two properties this is built around, and they are the two the manifest lets a
Feature choose between:

- **at-least-once** means the delivery is queued and retried, so the service
  must tolerate seeing an event twice. That is the right default and the
  default the reader applies, because the alternative silently loses an event
  somebody was charged for.
- **at-most-once** means the store tries and forgets, which is right for
  something advisory and wrong for anything else.

Delivery itself is deliberately out of this module. Whether it goes to Celery,
to a database-backed queue or to a thread depends on the store, and a reference
implementation that picked one would be telling every store to run it.
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

from .catalogue import is_known_event
from .contract import ExternalContract, external_features

logger = logging.getLogger(__name__)


def subscribers_for(event: str, feature_root: str | Path | None = None) -> list[tuple[ExternalContract, dict[str, Any]]]:
    """
    Who wants to hear about this event, and on what terms.

    Reads the registry every time rather than caching. A Feature disabled a
    second ago must stop receiving events now, not at the next restart — an
    entitlement that lapsed is a commercial fact and the store enforces it
    (docs/feature-delivery.md §2).
    """
    subscribers = []

    for contract in external_features(feature_root):
        for subscription in contract.webhooks:
            if subscription.get("event") == event:
                subscribers.append((contract, subscription))

    return subscribers


def publish(
    event: str,
    payload: dict[str, Any],
    feature_root: str | Path | None = None,
    deliver=None,
) -> int:
    """
    Hands one event to every Feature that subscribed to it.

    Returns how many deliveries were made, which is what a caller in the store's
    own code wants to log. An event nobody subscribed to costs one registry read
    and does nothing, which is the overwhelmingly common case.

    `deliver` is the transport, taking (contract, subscription, payload). The
    default writes to the store's delivery queue, **in the caller's
    transaction**: an order that rolls back must take its notifications with it,
    and a queue written after the commit loses events whenever the process dies
    in between.

    Sending is a separate process reading committed rows. A store that posted to
    somebody else's endpoint while holding a lock on its own orders table would
    be a store whose checkout stops when a third party is slow.
    """
    if not is_known_event(event):
        # Loudly, because this is the store's own code publishing something it
        # never declared. A Feature could not have subscribed to it, so nothing
        # will ever hear it, and the author almost certainly meant a name that
        # is on the list.
        logger.error(
            "The store published '%s', which is not in KNOWN_EVENTS. "
            "No external Feature can subscribe to it, so nothing will receive it.",
            event,
        )
        return 0

    subscribers = subscribers_for(event, feature_root)
    sent = 0

    for contract, subscription in subscribers:
        try:
            (deliver or _queue)(contract, subscription, payload)
            sent += 1
        except Exception:  # noqa: BLE001
            # One Feature's delivery failing must not stop the next one's. The
            # store's own transaction is long finished; this is fan-out.
            logger.exception(
                "Delivering '%s' to %s failed.", event, contract.slug
            )

    return sent


def _queue(contract: ExternalContract, subscription: dict[str, Any], payload: dict[str, Any]) -> None:
    """The default transport: write it down and let the worker deal with it."""
    from .delivery import enqueue

    enqueue(contract, subscription, payload)

    logger.info(
        "Queued %s for %s at %s (%s).",
        subscription.get("event"),
        contract.slug,
        contract.url_for(subscription.get("path", "/")),
        subscription.get("delivery", "at-least-once"),
    )
