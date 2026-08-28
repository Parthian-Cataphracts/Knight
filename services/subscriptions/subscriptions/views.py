"""
What the store's proxy forwards to.

Two surfaces, and the difference between them is who the store said was asking:

- ``/api/v1/subscriptions/…`` — a shopper managing their own. Scoped to the
  subject the store asserted, so a customer cannot read another customer's
  agreement by knowing its reference.
- ``/api/v1/admin/…`` — the merchant's side. Scoped to the store, and staff-only.

Both are scoped by store before anything else, because one deployment serves
every shop and a query that forgot would be a data leak rather than a bug
(``docs/adr/0033-api-driven-features.md``).
"""

from __future__ import annotations

from django.http import JsonResponse
from django.views.decorators.csrf import csrf_exempt
from django.views.decorators.http import require_POST

from knightlink.auth import body, signed

from . import services
from .models import Subscription


def _summary(summary) -> dict:
    return {
        "reference": summary.reference,
        "state": summary.state,
        "interval": summary.interval,
        "intervalCount": summary.interval_count,
        "amount": str(summary.amount),
        "currency": summary.currency,
        "nextRunAt": summary.next_run_at.isoformat() if summary.next_run_at else None,
        "periodsBilled": summary.periods_billed,
        "paidToDate": str(summary.paid_to_date),
        "provider": summary.provider,
        "location": summary.location,
    }


def _mine(request):
    """
    The subscriptions the caller may see.

    Store first, then subject. A staff caller sees the store's; a customer sees
    only their own, and "their own" is the shopper id the *store* asserted
    rather than one this service was told in a query parameter.
    """
    found = Subscription.objects.filter(store=request.knight.store)

    if not request.knight.is_staff:
        subject = request.knight.subject

        if not subject:
            return found.none()

        found = found.filter(source_shopper_id=subject)

    return found


# --- The public face --------------------------------------------------------


@signed
def public(request):
    """
    What a storefront can show somebody who is not signed in.

    Signed like everything else — the store still proves it is the store — but
    with no identity asserted, so nothing here may be about a person. What it
    returns is the shape of the offer: which currency this merchant bills in and
    what intervals they support.
    """
    from . import config

    return JsonResponse(
        {
            "service": "subscriptions",
            "store": request.knight.store.slug,
            "currency": config.currency(),
            "intervals": ["daily", "weekly", "monthly", "yearly"],
            "acceptingNew": request.knight.store.enabled,
        }
    )


# --- The shopper's side -----------------------------------------------------


@signed(require="customer")
def index(request):
    """Everything this shopper has with this store."""
    return JsonResponse(
        {
            "items": [
                _summary(services.summarise(subscription))
                for subscription in _mine(request).prefetch_related("periods")[:100]
            ]
        }
    )


@signed(require="customer")
def detail(request, reference: str):
    subscription = _mine(request).filter(reference=reference).first()

    if subscription is None:
        # The same answer whether it does not exist or belongs to somebody else.
        # Distinguishing them would let a shopper enumerate a merchant's
        # references by watching which ones came back 403.
        return JsonResponse({"detail": "No such subscription."}, status=404)

    summary = services.summarise(subscription)

    return JsonResponse(
        {
            **_summary(summary),
            "lines": [
                {"sku": line.sku, "name": line.name, "quantity": line.quantity, "unitPrice": str(line.unit_price)}
                for line in subscription.lines.all()
            ],
            "periods": [
                {
                    "sequence": period.sequence,
                    "state": period.state,
                    "startsOn": period.starts_on.isoformat(),
                    "endsOn": period.ends_on.isoformat(),
                    "amount": str(period.amount),
                }
                for period in subscription.periods.order_by("sequence")
            ],
        }
    )


def _act(request, action, reference: str, *, actor: str):
    subscription = _mine(request).filter(reference=reference).first()

    if subscription is None:
        return JsonResponse({"detail": "No such subscription."}, status=404)

    payload = body(request)

    try:
        summary = action(
            request.knight.store,
            reference,
            actor=actor,
            **({"reason": str(payload.get("reason") or "")[:500]} if action is not services.resume else {}),
        )
    except services.InvalidTransition as refusal:
        return JsonResponse({"detail": str(refusal), "errorCode": "invalid_transition"}, status=409)
    except services.SubscriptionError as refusal:
        return JsonResponse({"detail": str(refusal), "errorCode": "refused"}, status=400)

    return JsonResponse(_summary(summary))


@csrf_exempt
@require_POST
@signed(require="customer")
def pause(request, reference: str):
    return _act(request, services.pause, reference, actor=f"shopper:{request.knight.subject}")


@csrf_exempt
@require_POST
@signed(require="customer")
def resume(request, reference: str):
    return _act(request, services.resume, reference, actor=f"shopper:{request.knight.subject}")


@csrf_exempt
@require_POST
@signed(require="customer")
def cancel(request, reference: str):
    return _act(request, services.cancel, reference, actor=f"shopper:{request.knight.subject}")


# --- The merchant's side ----------------------------------------------------


@signed(require="staff")
def admin_index(request):
    """
    Every subscription this store has, newest first.

    Staff-only, and enforced twice — once by the store's proxy and once here.
    The duplication is deliberate: one of the two checks is somebody else's
    code, and a mis-wired proxy should not be the only thing between a shopper
    and a merchant's whole book.
    """
    found = Subscription.objects.filter(store=request.knight.store)

    if state := request.GET.get("state"):
        found = found.filter(state=state)

    return JsonResponse(
        {
            "items": [
                {
                    **_summary(services.summarise(subscription)),
                    "displayName": subscription.display_name,
                    "email": subscription.email,
                }
                for subscription in found.prefetch_related("periods")[:200]
            ]
        }
    )


@signed(require="staff")
def admin_due(request):
    """What the billing worker will charge on its next pass, for this store."""
    return JsonResponse(
        {
            "items": [
                {
                    "reference": subscription.reference,
                    "displayName": subscription.display_name,
                    "amount": str(subscription.amount),
                    "nextRunAt": subscription.next_run_at.isoformat() if subscription.next_run_at else None,
                }
                for subscription in services.due(request.knight.store)[:200]
            ]
        }
    )


@signed(require="staff")
def admin_detail(request, reference: str):
    try:
        summary = services.summarise(reference, store=request.knight.store)
    except services.UnknownSubscription:
        return JsonResponse({"detail": "No such subscription."}, status=404)

    return JsonResponse(
        {
            **_summary(summary),
            "history": [
                {
                    "from": event.from_state,
                    "to": event.to_state,
                    "actor": event.actor,
                    "reason": event.reason,
                    "at": event.occurred_at.isoformat(),
                }
                for event in services.history(request.knight.store, reference)
            ],
        }
    )
