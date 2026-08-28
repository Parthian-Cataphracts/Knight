"""
The four order events this service asked the store to forward.

Every one of them is **past tense**, and that is the contract rather than a
naming style: a subscriber is being told something has already happened, not
asked whether it may. A third party that could refuse an order would be a
checkout that goes down when somebody else's server does
(``docs/adr/0033-api-driven-features.md``).

Two properties every receiver here has, and both come from the store's manifest
declaring ``delivery: at-least-once``:

- **Idempotent.** The store queues and retries, so a delivery arriving twice is
  the normal case rather than a bug. Each handler below is written so the second
  one changes nothing.
- **Fast, and never a veto.** The store has already committed its own
  transaction. Anything slow here is a delivery that times out and gets retried,
  which is the same work done twice.
"""

from __future__ import annotations

import logging

from django.db import transaction
from django.http import JsonResponse
from django.views.decorators.csrf import csrf_exempt
from django.views.decorators.http import require_POST

from knightlink.auth import body, signed

from . import services
from .models import Subscription

logger = logging.getLogger(__name__)


def _received(what: str, **extra) -> JsonResponse:
    return JsonResponse({"received": True, "action": what, **extra})


@csrf_exempt
@require_POST
@signed
def order_placed(request):
    """
    An order was placed. If it was one of ours, record that the period it paid
    for has an order against it.

    A store places the order for a period this service asked it to bill, then
    tells us the number. Until that arrives, the period is paid and has no order
    against it — which is a real state and one `periods_awaiting_orders` exists
    to report on.
    """
    payload = body(request)
    reference = str(payload.get("subscriptionReference") or "").strip()

    if not reference:
        # Most orders in a shop have nothing to do with a subscription. Saying
        # so plainly beats a 400 that would make the store retry forever.
        return _received("ignored", reason="not a subscription order")

    try:
        sequence = int(payload.get("periodSequence") or 0)
        number = int(payload.get("orderNumber") or 0)
    except (TypeError, ValueError):
        return JsonResponse({"detail": "periodSequence and orderNumber must be numbers."}, status=400)

    if not sequence or not number:
        return JsonResponse({"detail": "periodSequence and orderNumber are required."}, status=400)

    try:
        services.record_order(request.knight.store, reference, sequence, number)
    except services.UnknownSubscription:
        # A store telling us about a subscription we have never heard of is a
        # store that is confused, or one whose data was restored from a backup
        # older than ours. Neither is fixed by retrying, so it is not a 5xx.
        logger.warning("Order for unknown subscription '%s' from %s.", reference, request.knight.store.slug)
        return _received("ignored", reason="unknown subscription")
    except services.SubscriptionError as refusal:
        return JsonResponse({"detail": str(refusal)}, status=409)

    return _received("order recorded", reference=reference, sequence=sequence)


@csrf_exempt
@require_POST
@signed
def order_paid(request):
    """
    An order was paid.

    For a subscription order this is confirmation the money arrived, and the
    period was already marked paid when the charge succeeded — so there is
    nothing to change and saying so is the honest answer. It is subscribed to
    anyway because a store that pays out of band (a bank transfer a merchant
    reconciles by hand) is a case this service will have to handle, and the
    subscription is the place that will land.
    """
    payload = body(request)
    reference = str(payload.get("subscriptionReference") or "").strip()

    if not reference:
        return _received("ignored", reason="not a subscription order")

    return _received("noted", reference=reference)


@csrf_exempt
@require_POST
@signed
def order_cancelled(request):
    """
    An order was cancelled. If it was a subscription's first order, the
    subscription goes with it.

    Only the first: cancelling one month's delivery is not cancelling the
    agreement, and treating it as one would end a merchant's revenue because a
    shopper skipped a box. The store says which it meant by sending
    ``cancelSubscription``.
    """
    payload = body(request)
    reference = str(payload.get("subscriptionReference") or "").strip()

    if not reference or not payload.get("cancelSubscription"):
        return _received("ignored", reason="not a subscription cancellation")

    try:
        with transaction.atomic():
            services.cancel(
                request.knight.store,
                reference,
                actor=f"store:{request.knight.store.slug}",
                reason=str(payload.get("reason") or "the order was cancelled")[:500],
            )
    except services.UnknownSubscription:
        return _received("ignored", reason="unknown subscription")
    except services.InvalidTransition:
        # Already cancelled. The delivery arrived twice, which is exactly what
        # at-least-once means, and the second one must not be an error.
        return _received("already cancelled", reference=reference)
    except services.SubscriptionError as refusal:
        return JsonResponse({"detail": str(refusal)}, status=409)

    return _received("cancelled", reference=reference)


@csrf_exempt
@require_POST
@signed
def order_refunded(request):
    """
    An order was refunded.

    Recorded against the subscription's history and nothing more. Whether a
    refund should end an agreement is a merchant's policy and not this service's
    to assume — a goodwill refund on a bad delivery is the common case, and
    cancelling somebody's subscription over it would be worse than doing
    nothing.
    """
    payload = body(request)
    reference = str(payload.get("subscriptionReference") or "").strip()

    if not reference:
        return _received("ignored", reason="not a subscription order")

    subscription = Subscription.objects.filter(
        store=request.knight.store, reference=reference
    ).first()

    if subscription is None:
        return _received("ignored", reason="unknown subscription")

    subscription.events.create(
        from_state=subscription.state,
        to_state=subscription.state,
        actor=f"store:{request.knight.store.slug}",
        reason=f"order {payload.get('orderNumber') or '?'} was refunded",
    )

    return _received("noted", reference=reference)
