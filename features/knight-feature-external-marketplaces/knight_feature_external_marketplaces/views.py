"""
The integration endpoints.

JSON only. Most of it is the operator's view of a queue — what is waiting, what
is stuck, what the two sides disagree about — and one of them is different in
kind from every other endpoint in this catalogue: **`webhook` is the only place
in KNIGHT's Features where somebody else's server talks to a store.**

That endpoint is written accordingly:

- it **records and returns**, and does nothing else. Acting on the message is the
  store's job, on the store's schedule, so a partner's timeout is never caused by
  this store's own work;
- a **redelivery answers 200**, because that is what makes a partner stop
  retrying. Answering 409 to a duplicate teaches their retry logic to try harder;
- it **never echoes the payload back**, and never quotes the connection's
  credential in an error.

Authenticating it is deliberately the **store's** business and not this
Feature's. Every partner signs webhooks differently — an HMAC header here, a
shared secret there, mutual TLS at the serious end — and a Feature that invented
one scheme would either be wrong for every partner or be a checkbox nobody ticks.
The store mounts this behind whatever its partner requires. That is stated here
rather than left to be discovered, and it is repeated in the manifest and in the
verification notes.
"""

from __future__ import annotations

import json

from django.http import JsonResponse
from django.views.decorators.csrf import csrf_exempt
from django.views.decorators.http import require_http_methods

from . import config, services


def index(request):
    """Every connection, with credentials by presence and never by value."""
    return JsonResponse(
        {
            "connections": services.connections(kind=request.GET.get("kind", "")),
            "adapters": _adapters(),
            "configuration": config.describe(),
        }
    )


def connection(request, slug: str):
    """One connection, and what is queued against it."""
    try:
        described = services.describe(slug)
    except services.UnknownConnection:
        return JsonResponse({"slug": slug, "found": False}, status=404)

    return JsonResponse(
        {
            "found": True,
            **described,
            "messages": [_message(message) for message in services.messages(slug=slug, limit=50)],
        }
    )


def queue(request):
    """
    How much is waiting, by state.

    The number to put on a dashboard: a queue that is growing is a partner that
    is down, and it shows here before it shows anywhere else.
    """
    return JsonResponse(
        {
            "depth": services.queue_depth(),
            "pendingInbound": [_message(message) for message in services.pending_inbound(limit=50)],
        }
    )


def stuck(request):
    """What has used its attempts and is waiting for a person."""
    return JsonResponse({"abandoned": [_message(message) for message in services.abandoned()]})


def differences(request):
    """What the two sides disagree about and nobody has decided yet."""
    return JsonResponse(
        {
            "differences": [
                {
                    "id": difference.pk,
                    "connection": difference.run.connection.slug,
                    "kind": difference.kind,
                    "localReference": difference.local_reference,
                    "remoteId": difference.remote_id,
                    "detail": difference.detail,
                    "raisedAt": difference.created_at.isoformat(),
                }
                for difference in services.open_differences()
            ]
        }
    )


@csrf_exempt
@require_http_methods(["POST"])
def webhook(request, slug: str):
    """
    Takes in one event from a partner.

    Records it and returns. See the module docstring for why it does nothing
    else, why a duplicate is a 200, and why authenticating this is the store's
    job rather than this Feature's.

    CSRF-exempt because the caller is a server with no session and no cookie —
    stated explicitly so that nobody removes the decorator wondering why it is
    there, and so that nobody mistakes it for this endpoint being unprotected.
    Protection is whatever the store mounts in front of it.
    """
    payload = _body(request)

    try:
        message = services.receive(
            slug,
            kind=str(payload.get("type") or payload.get("kind") or "event"),
            external_id=str(payload.get("id") or payload.get("eventId") or ""),
            payload=payload,
            subject_type=str(payload.get("subjectType") or ""),
            subject_id=str(payload.get("subjectId") or ""),
        )
    except services.UnknownConnection:
        return JsonResponse({"error": "No such connection."}, status=404)
    except services.DuplicateMessage:
        # 200, deliberately. This is what makes a partner's retry logic stop, and
        # answering 409 teaches it to try harder.
        return JsonResponse({"accepted": True, "duplicate": True})
    except services.MarketplaceError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    return JsonResponse({"accepted": True, "duplicate": False, "id": message.id})


@require_http_methods(["POST"])
def replay(request, message_id: int):
    """Puts an abandoned message back in the queue, after a person fixed something."""
    try:
        message = services.replay(message_id)
    except services.MarketplaceError as exc:
        return JsonResponse({"error": str(exc)}, status=400)

    return JsonResponse(_message(message))


def _adapters() -> list[str]:
    from . import adapters

    return adapters.known()


def _message(message) -> dict:
    return {
        "id": message.id,
        "connection": message.connection,
        "direction": message.direction,
        "kind": message.kind,
        "state": message.state,
        "attempts": message.attempts,
        "externalId": message.external_id,
        "remoteReference": message.remote_reference,
        "subjectType": message.subject[0],
        "subjectId": message.subject[1],
        "lastError": message.last_error,
    }


def _body(request) -> dict:
    try:
        document = json.loads(request.body or b"{}")
    except (ValueError, TypeError):
        return {}

    return document if isinstance(document, dict) else {}
