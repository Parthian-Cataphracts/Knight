"""
What KNIGHT may tell this service: who the stores are, and what they may sign
with.

The second caller. Until phase 24 there was one — a store, signing with a secret
an operator had typed into both ends — and everything about that arrangement was
correct for one store and an incident at ten: nobody could rotate a secret
without downtime, nobody could say which stores shared one, and withdrawing an
entitlement stopped the store forwarding without stopping this service
answering.

These four endpoints are the other half of that. KNIGHT issues the secret,
registers the store here, rotates it with an overlap, and revokes it when the
entitlement goes away. Four properties hold across all of them:

- **KNIGHT authenticates as KNIGHT**, with its own secret, never as a store. A
  store cannot prove it is a store before it has a secret, and issuing that
  secret is what this is for.
- **Nothing here returns a secret.** They are written and never read back — a
  control plane that could read them back would be a second place they leak
  from, and KNIGHT holds its own copy already.
- **Everything is idempotent.** KNIGHT retries a call it is not sure arrived, and
  a retry that issued a second secret or a second store would be worse than the
  uncertainty.
- **Revocation is immediate.** Not "at the next configuration sync": the next
  request from that store is refused, by this service, whatever the store still
  believes.
"""

from __future__ import annotations

import json
import logging
import uuid

from django.conf import settings
from django.db import transaction
from django.http import JsonResponse
from django.views.decorators.csrf import csrf_exempt
from django.views.decorators.http import require_POST

from .models import Store
from .signing import Unsigned, verify_control_plane

logger = logging.getLogger(__name__)

#: The shortest secret this service will accept from KNIGHT.
#:
#: Refused rather than accepted-and-warned. A short shared secret is a brute
#: force away from being no secret at all, and the moment to say so is while
#: something is still listening.
MINIMUM_SECRET_LENGTH = 32


def control(view):
    """Refuses anything KNIGHT did not sign."""

    from functools import wraps

    @wraps(view)
    def wrapper(request, *args, **kwargs):
        try:
            verify_control_plane(request)
        except Unsigned as refusal:
            return JsonResponse({"detail": refusal.reason, "errorCode": refusal.code}, status=401)

        return view(request, *args, **kwargs)

    return wrapper


def _body(request) -> dict:
    if not request.body:
        return {}

    try:
        parsed = json.loads(request.body.decode("utf-8"))
    except (ValueError, UnicodeDecodeError):
        return {}

    return parsed if isinstance(parsed, dict) else {}


def _refused(detail: str, code: str = "invalid", status: int = 400) -> JsonResponse:
    return JsonResponse({"detail": detail, "errorCode": code}, status=status)


def _store_of(payload: dict, key: str = "storeId") -> Store | None:
    value = str(payload.get(key) or "").strip()

    try:
        uuid.UUID(value)
    except (ValueError, AttributeError, TypeError):
        return None

    return Store.objects.filter(store_id=value).first()


def _described(store: Store) -> dict:
    """
    What a control-plane call answers with.

    No secret, and no way to ask for one. What KNIGHT needs back is whether the
    store exists here, whether it is enabled, and how many secrets are currently
    live — which is enough to tell a rotation that worked from one that left the
    store with two valid secrets for ever.
    """
    return {
        "storeId": str(store.store_id),
        "slug": store.slug,
        "enabled": store.enabled,
        "usableSecrets": len(store.usable_secrets()),
    }


@csrf_exempt
@require_POST
@control
def register(request):
    """
    Registers a store, or updates the one already here.

    Idempotent on the store id, which is the name all three systems agree on. A
    slug is a name a merchant can change; registering by it would make a rename
    look like a new shop.

    The secret arrives with the registration because the two facts are one fact:
    a store with no secret is registered and unable to say anything.
    """
    payload = _body(request)
    store_id = str(payload.get("storeId") or "").strip()

    try:
        uuid.UUID(store_id)
    except (ValueError, AttributeError, TypeError):
        return _refused("storeId must be the store's id in KNIGHT.")

    slug = str(payload.get("slug") or "").strip()[:120]

    if not slug:
        return _refused("slug is required.")

    secret = str(payload.get("secret") or "")

    if len(secret) < MINIMUM_SECRET_LENGTH:
        return _refused(
            f"A shared secret must be at least {MINIMUM_SECRET_LENGTH} characters.",
            "secret.too_short",
        )

    with transaction.atomic():
        store, created = Store.objects.get_or_create(
            store_id=store_id,
            defaults={"slug": slug, "base_url": str(payload.get("baseUrl") or "")[:200]},
        )

        store.slug = slug

        if "baseUrl" in payload:
            store.base_url = str(payload.get("baseUrl") or "")[:200]

        if isinstance(payload.get("settings"), dict):
            store.settings = payload["settings"]

        # A registration re-enables a store that had been revoked. That is the
        # point: re-entitling a customer must not need somebody to remember a
        # second step.
        store.enabled = bool(payload.get("enabled", True))
        store.save()

        store.rotate_to(
            secret,
            overlap_seconds=_overlap(payload),
            issued_by="knight",
        )

    logger.info("KNIGHT registered store '%s' (%s).", store.slug, "new" if created else "existing")

    return JsonResponse({**_described(store), "created": created}, status=201 if created else 200)


@csrf_exempt
@require_POST
@control
def rotate(request):
    """
    Issues a store a new secret, without cutting off the old one.

    The whole reason secrets are rows. Both remain valid for the overlap window,
    so KNIGHT can deliver the new configuration to a store that is already
    running and nothing in flight is refused. An overlap of zero is allowed and
    is what a leak needs — but it is not the default, because the default should
    be the one that does not drop anybody's request.
    """
    payload = _body(request)
    store = _store_of(payload)

    if store is None:
        return _refused("This service does not know that store.", "store.unknown", status=404)

    secret = str(payload.get("secret") or "")

    if len(secret) < MINIMUM_SECRET_LENGTH:
        return _refused(
            f"A shared secret must be at least {MINIMUM_SECRET_LENGTH} characters.",
            "secret.too_short",
        )

    store.rotate_to(secret, overlap_seconds=_overlap(payload), issued_by="knight")

    logger.info("KNIGHT rotated the secret for store '%s'.", store.slug)

    return JsonResponse(_described(store))


@csrf_exempt
@require_POST
@control
def revoke(request):
    """
    Stops a store reaching this service at all.

    What a withdrawn entitlement needs, and the half that was missing: the store
    stops forwarding because its registry says the Feature is disabled, and this
    stops the service answering a store whose registry is stale, wrong, or
    restored from a backup.

    Both facts are set — disabled, and every secret revoked — because they answer
    different questions and an incident wants both.
    """
    payload = _body(request)
    store = _store_of(payload)

    if store is None:
        # Not an error. KNIGHT withdrawing an entitlement from a store this
        # service never had is the outcome it wanted.
        return JsonResponse({"revoked": False, "reason": "unknown store"})

    with transaction.atomic():
        store.enabled = False
        store.save(update_fields=["enabled", "updated_at"])
        ended = store.revoke_secrets()

    logger.info("KNIGHT revoked store '%s' (%s secret(s) ended).", store.slug, ended)

    return JsonResponse({**_described(store), "revoked": True, "secretsEnded": ended})


@csrf_exempt
@require_POST
@control
def describe(request):
    """
    What this service currently believes about one store.

    A POST because it is signed over a body like everything else here, and
    because reading a store's registration is a control-plane act rather than a
    public one. It carries no secret and no subscription data: it exists so
    KNIGHT can reconcile — "does the service agree this store is entitled" — and
    a reconciliation that could read secrets would be a reconciliation worth
    stealing.
    """
    payload = _body(request)
    store = _store_of(payload)

    if store is None:
        return JsonResponse({"registered": False}, status=404)

    return JsonResponse({"registered": True, **_described(store)})


def _overlap(payload: dict) -> int:
    """
    How long the previous secrets keep working, from the caller or the setting.

    Clamped rather than trusted. An overlap of a week from a typo would leave a
    replaced secret valid for a week, and this is the one number where a mistake
    is silent.
    """
    if "overlapSeconds" not in payload:
        return settings.KNIGHT_DEFAULT_OVERLAP_SECONDS

    try:
        asked = int(payload["overlapSeconds"])
    except (TypeError, ValueError):
        return settings.KNIGHT_DEFAULT_OVERLAP_SECONDS

    return max(0, min(asked, 24 * 3600))
