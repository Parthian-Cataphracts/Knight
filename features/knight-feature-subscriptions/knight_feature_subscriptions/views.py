"""
The subscription endpoints.

JSON only, and read-mostly. **There is no endpoint that charges anybody**, and
that absence is the most important design decision in this file. Billing happens
on a schedule the Feature owns, from a worker; an endpoint that took money would
be a way to charge a shopper by sending a request, and no amount of
authentication makes that a good idea in a package a store installs.

The three writes are the three things a person genuinely does: pause, resume and
cancel. All of them stop or restart money rather than moving it, which is the
right shape for the only writes a subscription surface should have.
"""

from __future__ import annotations

import json

from django.http import JsonResponse
from django.views.decorators.http import require_http_methods

from . import config, services


def index(request):
    """Every subscription, with the figures a merchant reads first."""
    from .models import Subscription

    found = Subscription.objects.all()
    state = request.GET.get("state", "")

    if state:
        found = found.filter(state=state)

    return JsonResponse(
        {
            "subscriptions": [
                _summary(services.summarise(subscription))
                for subscription in found.order_by("-created_at")[:200]
            ]
        }
    )


def detail(request, reference: str):
    """
    One subscription, its periods and every attempt made against them.

    The attempts are included rather than hidden behind another call, because
    this is the page somebody opens when a shopper says they were charged twice,
    and making them click again to find out is making them wait during an
    argument.
    """
    try:
        summary = services.summarise(reference)
        periods = services.periods(reference)
    except services.UnknownSubscription:
        return JsonResponse({"reference": reference, "found": False}, status=404)

    return JsonResponse(
        {
            "found": True,
            **_summary(summary),
            "lines": [
                {
                    "sku": line.sku,
                    "name": line.name,
                    "quantity": line.quantity,
                    "unitPrice": str(line.unit_price),
                }
                for line in services.lines(reference)
            ],
            "periods": [
                {
                    "sequence": period.sequence,
                    "startsOn": period.starts_on.isoformat(),
                    "endsOn": period.ends_on.isoformat(),
                    "amount": str(period.amount),
                    "state": period.state,
                    "attempts": [
                        {
                            "attempt": attempt.attempt,
                            "outcome": attempt.outcome,
                            "provider": attempt.provider,
                            "reference": attempt.provider_reference,
                            "detail": attempt.detail,
                            "occurredAt": attempt.occurred_at.isoformat(),
                        }
                        for attempt in period.attempts.all()
                    ],
                }
                for period in periods
            ],
            "history": [
                {
                    "from": event.from_state,
                    "to": event.to_state,
                    "actor": event.actor,
                    "reason": event.reason,
                    "occurredAt": event.occurred_at.isoformat(),
                }
                for event in services.history(reference)
            ],
        }
    )


def due(request):
    """
    What the next billing run would charge.

    A read, and the one a merchant should look at before trusting a schedule. It
    charges nothing: seeing what is about to happen must never be the thing that
    makes it happen.
    """
    return JsonResponse(
        {
            "due": [
                {
                    "reference": subscription.reference,
                    "state": subscription.state,
                    "amount": str(subscription.amount),
                    "currency": subscription.currency,
                    "nextRunAt": subscription.next_run_at.isoformat() if subscription.next_run_at else None,
                }
                for subscription in services.due()[:200]
            ]
        }
    )


def configuration(request):
    """
    What this Feature has been configured with.

    Secrets are reported as present or absent and never by value. A payment key
    echoed by a debugging endpoint is a payment key in a log aggregator.
    """
    return JsonResponse(config.describe())


@require_http_methods(["POST"])
def pause(request, reference: str):
    """Stops billing without ending the agreement."""
    return _act(services.pause, reference, _body(request))


@require_http_methods(["POST"])
def resume(request, reference: str):
    """Starts billing again, from now rather than from where it stopped."""
    return _act(services.resume, reference, _body(request))


@require_http_methods(["POST"])
def cancel(request, reference: str):
    """Ends the agreement, refunding nothing and deleting nothing."""
    return _act(services.cancel, reference, _body(request))


def _act(action, reference: str, payload: dict):
    try:
        summary = action(reference, actor=payload.get("actor", ""), **_reason(action, payload))
    except services.UnknownSubscription:
        return JsonResponse({"reference": reference, "found": False}, status=404)
    except services.InvalidTransition as exc:
        # 409 rather than 400: the request was well formed and the world was not
        # in the state it assumed, which a storefront needs to tell apart to say
        # "this is already cancelled" rather than "something went wrong".
        return JsonResponse({"error": str(exc)}, status=409)
    except services.SubscriptionError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    return JsonResponse(_summary(summary))


def _reason(action, payload: dict) -> dict:
    """`resume` takes no reason; the other two do."""
    return {} if action is services.resume else {"reason": payload.get("reason", "")}


def _summary(summary) -> dict:
    return {
        "reference": summary.reference,
        "state": summary.state,
        "interval": summary.interval,
        "intervalCount": summary.interval_count,
        "currency": summary.currency,
        "amount": str(summary.amount),
        "nextRunAt": summary.next_run_at.isoformat() if summary.next_run_at else None,
        "periodsBilled": summary.periods_billed,
        "paidToDate": str(summary.paid_to_date),
        "provider": summary.provider,
        "location": summary.location,
        "isBillable": summary.is_billable,
    }


def _body(request) -> dict:
    try:
        return json.loads(request.body or b"{}")
    except (ValueError, TypeError):
        return {}
