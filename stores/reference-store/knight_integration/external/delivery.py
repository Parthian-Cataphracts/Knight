"""
The queue that makes ``at-least-once`` a guarantee rather than a word.

Until this existed, ``bus.publish`` resolved subscribers and handed each one to a
callable that logged and did nothing, because the reference store had no
transport and inventing one would have been telling every store to run Celery.

This is a transport that assumes nothing: a table, a worker, and an exponential
retry. A store that has Celery or SQS replaces this module and keeps the rest;
what it must not replace is the two properties.

**A delivery is written in the same transaction as the thing it is about.**
`publish` is called inside the store's own transaction, so an order that rolls
back takes its notifications with it. A queue written after the commit loses
events whenever the process dies in between, and a queue written before it sends
notifications about orders that never happened. The first is a lost renewal; the
second is a customer charged for an order they do not have.

**Sending happens outside that transaction.** The worker is a separate process
reading committed rows. A store that posted to somebody else's HTTP endpoint
while holding a row lock on its own orders table would be a store whose checkout
stops when a third party is slow.
"""

from __future__ import annotations

import json
import logging
from datetime import timedelta

from django.db import models, transaction
from django.utils import timezone

logger = logging.getLogger(__name__)

#: How long to wait before each attempt, in seconds. Doubling, and it stops.
#:
#: Seven attempts over roughly twelve hours. Long enough that a deploy or a
#: short outage at the other end is invisible; short enough that a service which
#: has been down all day is dead-lettered rather than retried for ever, because
#: an event delivered eleven hours late is usually worse than one that was
#: escalated.
BACKOFF_SECONDS = [0, 30, 120, 600, 1800, 7200, 21600]

MAX_ATTEMPTS = len(BACKOFF_SECONDS)


class DeliveryState(models.TextChoices):
    PENDING = "Pending", "Pending"
    DELIVERED = "Delivered", "Delivered"

    #: Every attempt used. Kept, never deleted: a dead letter is the record that
    #: a Feature a merchant pays for did not hear something, and deleting it
    #: would make the failure invisible at exactly the moment somebody needs to
    #: know about it.
    DEAD = "DeadLettered", "Dead-lettered"


class WebhookDelivery(models.Model):
    """One event, on its way to one Feature's service."""

    feature_slug = models.CharField(max_length=100, db_index=True)
    event = models.CharField(max_length=100, db_index=True)

    #: Where it is going, resolved when the delivery was queued rather than when
    #: it is sent. A Feature upgraded between the two must not silently
    #: redirect an event that was already accepted for delivery.
    url = models.URLField(max_length=500)

    payload = models.JSONField(default=dict)

    #: `at-least-once` or `at-most-once`, from the Feature's manifest. The only
    #: thing that decides whether a failure is retried.
    guarantee = models.CharField(max_length=20, default="at-least-once")

    state = models.CharField(max_length=20, choices=DeliveryState, default=DeliveryState.PENDING)
    attempts = models.PositiveSmallIntegerField(default=0)

    #: When the next attempt becomes due. **This is the clock**: due-ness is this
    #: field against the wall clock, never "has the worker run".
    next_attempt_at = models.DateTimeField(default=timezone.now, db_index=True)

    last_status = models.PositiveSmallIntegerField(null=True, blank=True)
    last_error = models.CharField(max_length=500, blank=True, default="")

    created_at = models.DateTimeField(auto_now_add=True)
    delivered_at = models.DateTimeField(null=True, blank=True)

    class Meta:
        db_table = "knight_webhook_delivery"
        ordering = ("next_attempt_at", "id")
        indexes = [
            # The only query the worker makes.
            models.Index(fields=["state", "next_attempt_at"], name="knight_delivery_due"),
        ]

    def __str__(self) -> str:
        return f"{self.event} -> {self.feature_slug} ({self.state})"

    @property
    def is_final(self) -> bool:
        return self.state in {DeliveryState.DELIVERED, DeliveryState.DEAD}


def enqueue(contract, subscription: dict, payload: dict) -> WebhookDelivery:
    """
    Writes one delivery. Called by the bus, inside the caller's transaction.

    Does no HTTP. That is the whole point of the split: the store commits its
    order and the worker deals with somebody else's server being slow.
    """
    return WebhookDelivery.objects.create(
        feature_slug=contract.slug,
        event=str(subscription.get("event") or ""),
        url=contract.url_for(subscription.get("path", "/")),
        payload=payload,
        guarantee=str(subscription.get("delivery") or "at-least-once"),
    )


def due(now=None, limit: int = 100):
    """The deliveries whose next attempt has come round."""
    return WebhookDelivery.objects.filter(
        state=DeliveryState.PENDING,
        next_attempt_at__lte=now or timezone.now(),
    ).order_by("next_attempt_at", "id")[:limit]


def send_due(now=None, limit: int = 100, sender=None) -> dict[str, int]:
    """
    Attempts every due delivery. The entrypoint of the worker.

    Each in its own transaction, deliberately: one service being down must not
    roll back the twenty deliveries that already succeeded.
    """
    now = now or timezone.now()
    counts = {"delivered": 0, "retrying": 0, "dead": 0}

    for delivery in list(due(now, limit)):
        outcome = attempt(delivery, now=now, sender=sender)
        counts[outcome] = counts.get(outcome, 0) + 1

    return counts


def attempt(delivery: WebhookDelivery, now=None, sender=None) -> str:
    """
    One attempt at one delivery. Returns `delivered`, `retrying` or `dead`.

    The row is locked and re-read inside the transaction, so two workers running
    at once cannot both send the same event. That is not a hypothetical: the
    obvious way to catch up after an outage is to start a second worker.
    """
    now = now or timezone.now()

    with transaction.atomic():
        fresh = WebhookDelivery.objects.select_for_update().filter(pk=delivery.pk).first()

        if fresh is None or fresh.is_final:
            return "retrying"

        fresh.attempts += 1

        try:
            status = (sender or _post)(fresh)
        except Exception as exc:  # noqa: BLE001 - any failure is a failed attempt
            status = None
            fresh.last_error = str(exc)[:500]
        else:
            fresh.last_status = status
            fresh.last_error = ""

        # 2xx is delivered. Everything else is not, including a 4xx: a service
        # answering 400 to an event it asked for is a service with a bug, and
        # dropping the event silently would hide it.
        if status is not None and 200 <= status < 300:
            fresh.state = DeliveryState.DELIVERED
            fresh.delivered_at = now
            fresh.save(update_fields=["state", "attempts", "last_status", "last_error", "delivered_at"])

            return "delivered"

        if fresh.guarantee == "at-most-once" or fresh.attempts >= MAX_ATTEMPTS:
            fresh.state = DeliveryState.DEAD
            fresh.save(update_fields=["state", "attempts", "last_status", "last_error"])

            logger.error(
                "Gave up delivering %s to %s after %s attempt(s): %s",
                fresh.event,
                fresh.feature_slug,
                fresh.attempts,
                fresh.last_error or fresh.last_status,
            )

            return "dead"

        fresh.next_attempt_at = now + timedelta(seconds=BACKOFF_SECONDS[fresh.attempts])
        fresh.save(update_fields=["attempts", "last_status", "last_error", "next_attempt_at"])

        return "retrying"


def _post(delivery: WebhookDelivery) -> int:
    """
    The HTTP attempt, signed the way the service expects.

    The signature is computed here rather than when the delivery was queued: a
    timestamp minted at queue time would be hours stale by the last retry, and
    the service would refuse it for being out of step with a clock that was
    right.
    """
    import requests

    from .contract import contract_of
    from .signing import secret_for, sign

    contract = _contract_for(delivery.feature_slug)

    if contract is None:
        raise LookupError(f"{delivery.feature_slug} is no longer installed on this store.")

    body = json.dumps(delivery.payload, default=str).encode("utf-8")
    path = "/" + delivery.url.split("/", 3)[-1] if delivery.url.count("/") > 2 else "/"

    headers = {
        "Content-Type": "application/json",
        "X-Knight-Store": contract.slug,
        "X-Knight-Event": delivery.event,
        "X-Knight-Delivery": str(delivery.pk),
        # The attempt number, so a service can tell a retry from a duplicate it
        # caused itself. It costs nothing and it is the first thing anybody
        # debugging a double-charge will want.
        "X-Knight-Attempt": str(delivery.attempts),
        **sign(secret_for(contract), "POST", path, body),
    }

    response = requests.post(delivery.url, data=body, headers=headers, timeout=10)

    return response.status_code


def _contract_for(slug: str):
    """
    The Feature's current registration, or None if it has been disabled.

    Read at send time rather than at queue time, because an entitlement that
    lapsed between the two must stop the delivery. The URL is the queued one;
    whether we are still allowed to use it is a question for now.
    """
    from .contract import external_features

    for contract in external_features():
        if contract.slug == slug:
            return contract

    return None
