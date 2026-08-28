"""
The store calling a Feature's service on its own behalf.

The third direction, and the one that did not exist until the billing loop
needed it. The store already forwards a shopper's request (``proxy``) and
announces its own events (``delivery``); both are driven by something that
happened. This is the store's own code, in a cron job, asking a service a
question and acting on the answer.

Two things make it different from the proxy, and both matter:

- **Nobody is asking.** There is no request, no session and no shopper. The
  store asserts ``staff`` and a subject of ``system``, because the caller is the
  store itself — and a service that saw a shopper id here would be a service
  told something untrue.
- **It is not on anybody's request path.** A slow service delays a cron run
  rather than a checkout, so the timeout is longer than the proxy's.

Everything else is the same machinery on purpose: the same signature over the
same canonical string, under the same per-Feature secret. A second way to
authenticate would be a second thing to get wrong.
"""

from __future__ import annotations

import json
import logging
from typing import Any

from .contract import ExternalContract, external_features
from .signing import secret_for, sign

logger = logging.getLogger(__name__)

#: Longer than the proxy's ten seconds. This runs in a job with time to wait,
#: and a batch that failed because a service took eleven seconds would be a
#: shopper's order not being placed for another hour.
TIMEOUT_SECONDS = 30


class ServiceCallFailed(RuntimeError):
    """
    The service did not answer, or answered with a refusal.

    One exception for both because a caller has the same job either way: stop,
    say what happened, and leave the work for the next run. `status` is None when
    nothing was answered at all.
    """

    def __init__(self, message: str, *, status: int | None = None, body: str = "") -> None:
        super().__init__(message)
        self.status = status
        self.body = body


def contract_for(slug: str) -> ExternalContract | None:
    """
    One external Feature's contract, or None when the store does not have it.

    Read every time and never cached: a Feature whose entitlement lapsed a
    second ago must stop being called now, not at the next restart.
    """
    for contract in external_features():
        if contract.slug == slug:
            return contract

    return None


def call(
    contract: ExternalContract,
    method: str,
    path: str,
    payload: dict[str, Any] | None = None,
    *,
    identity: str = "staff",
    subject: str = "system",
    timeout: int = TIMEOUT_SECONDS,
) -> dict[str, Any]:
    """
    One signed request to a Feature's service, and its JSON body.

    `path` is the path **on the service**, and it is what gets signed — the same
    discipline the proxy follows, and for the same reason: both ends build the
    canonical string independently, so signing anything other than the path the
    service will route makes every request fail verification with a signature
    that is perfectly correct about the wrong thing.
    """
    import requests

    method = method.upper()
    path = "/" + str(path).lstrip("/")
    body = json.dumps(payload).encode("utf-8") if payload is not None else b""

    headers = {
        "X-Knight-Feature": contract.slug,
        "X-Knight-Identity": identity,
        "X-Knight-Subject": subject,
    }

    if payload is not None:
        headers["Content-Type"] = "application/json"

    try:
        headers.update(sign(secret_for(contract), method, path, body))
    except LookupError as exc:
        # An unsigned request is not a fallback. A service that accepted one
        # would accept anybody's.
        raise ServiceCallFailed(str(exc)) from exc

    try:
        answer = requests.request(
            method,
            contract.url_for(path),
            data=body if body else None,
            headers=headers,
            timeout=timeout,
            allow_redirects=False,
        )
    except requests.RequestException as exc:
        raise ServiceCallFailed(f"{contract.slug} did not answer: {exc}") from exc

    if answer.status_code >= 400:
        # The service's own words, truncated. It is somebody else's string and
        # it goes into a log, never into a page.
        raise ServiceCallFailed(
            f"{contract.slug} answered {answer.status_code} to {method} {path}.",
            status=answer.status_code,
            body=answer.text[:500],
        )

    if not answer.content:
        return {}

    try:
        parsed = answer.json()
    except ValueError as exc:
        raise ServiceCallFailed(
            f"{contract.slug} answered {method} {path} with something that is not JSON.",
            status=answer.status_code,
            body=answer.text[:500],
        ) from exc

    return parsed if isinstance(parsed, dict) else {"items": parsed}
