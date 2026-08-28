"""
Forwarding a range of this store's URL space to a Feature's service.

The replacement for a mounted urlconf, and the difference that matters is who
runs the code: a mount ran the Feature's code in this process with this
process's database handle, and a proxy makes an HTTP request and returns what
comes back. Everything below exists to keep that difference honest.

**The shopper's own credentials never leave the store.** The store asserts *who
is asking* in a signed header and forwards nothing else: no session cookie, no
Authorization header, no CSRF token. A Feature's service holding a credential it
could replay against the store is the thing this avoids, and it is the single
most important line in this file.

**Only the declared methods, only the declared prefix.** Both come from a
manifest KNIGHT signed. A route that acquired a DELETE because nobody wrote a
method list is a read-only Feature that can now delete things.
"""

from __future__ import annotations

import logging
from pathlib import Path
from typing import Any

from .contract import ExternalContract, external_features
from .signing import secret_for, sign

logger = logging.getLogger(__name__)

#: Request headers this store will never forward, whatever a Feature asks for.
#:
#: Everything that carries the shopper's identity or the store's own. The
#: Feature is told who is asking by `X-Knight-Identity`, which the store signs;
#: anything it could replay is stripped.
NEVER_FORWARDED = frozenset(
    {
        "authorization",
        "cookie",
        "set-cookie",
        "x-csrftoken",
        "x-csrf-token",
        "proxy-authorization",
        "host",
        "content-length",
        "connection",
        "transfer-encoding",
    }
)

#: Response headers this store will not pass back to the browser.
#:
#: A Feature's service must not be able to set a cookie on the store's domain:
#: that is a session it did not issue, on an origin it does not own.
NEVER_RETURNED = frozenset(
    {
        "set-cookie",
        "transfer-encoding",
        "connection",
        "content-encoding",
        "content-length",
    }
)

#: How long the store waits. Short, because this is on a shopper's request path
#: and a Feature's service being slow must not become the store being slow.
TIMEOUT_SECONDS = 10


def proxy_urlpatterns(feature_root: str | Path | None = None, existing=None):
    """
    URL patterns for every enabled external Feature's declared prefixes.

    Built the same way `feature_urlpatterns` builds mounts for packages, and it
    reports the same collision for the same reason: the store's own route wins,
    and a delivered configuration must not be able to take over a route the shop
    already serves — but doing it silently would leave a Feature whose install
    succeeded and whose pages answer somebody else's view.
    """
    from django.urls import path, re_path

    from ..features.loader import _declared_prefixes

    patterns = []

    try:
        contracts = external_features(feature_root)
    except Exception:  # noqa: BLE001
        logger.exception("The feature registry could not be read; mounting no proxy routes.")
        return patterns

    taken = _declared_prefixes(existing)

    for contract in contracts:
        for route in contract.api_proxies:
            prefix = str(route.get("prefix") or "").lstrip("/")

            if not prefix:
                continue

            if prefix in taken:
                logger.error(
                    "Feature '%s' asks to proxy '%s', which this store already serves. "
                    "The store's own route wins, so this Feature will not answer there.",
                    contract.slug,
                    prefix,
                )

            patterns.append(
                re_path(
                    rf"^{prefix}(?P<remainder>.*)$",
                    _view_for(contract, route),
                    name=f"knight-proxy-{contract.slug}-{prefix.strip('/').replace('/', '-')}",
                )
            )

    return patterns


def _view_for(contract: ExternalContract, route: dict[str, Any]):
    methods = [str(method).upper() for method in (route.get("methods") or ["GET"])]
    identity = str(route.get("identity") or "anonymous")
    upstream = str(route.get("upstream") or "/")

    def view(request, remainder: str = ""):
        return forward(request, contract, remainder, upstream, methods, identity)

    view.csrf_exempt = True
    return view


def forward(
    request,
    contract: ExternalContract,
    remainder: str,
    upstream: str,
    methods: list[str],
    identity: str,
):
    """One request, forwarded. Split out so it can be tested without a URL conf."""
    import requests
    from django.http import HttpResponse, JsonResponse

    if request.method not in methods:
        # The store's 405, which never reaches the service. A method the
        # manifest did not declare is not a request this Feature may make.
        return JsonResponse(
            {"detail": f"{request.method} is not forwarded to {contract.slug}."},
            status=405,
        )

    if not _identity_satisfied(request, identity):
        return JsonResponse({"detail": "Not authorised."}, status=403)

    target = contract.url_for(f"{upstream.strip('/')}/{remainder.lstrip('/')}".strip("/"))
    body = request.body or b""

    headers = {
        name: value
        for name, value in _incoming_headers(request).items()
        if name.lower() not in NEVER_FORWARDED
    }

    # Who is asking, asserted by the store and signed. This is the only identity
    # the service ever sees, and it is the reason no credential is forwarded.
    headers["X-Knight-Store"] = contract.slug
    headers["X-Knight-Identity"] = identity
    headers["X-Knight-Subject"] = _subject(request, identity)

    try:
        headers.update(sign(secret_for(contract), request.method, f"/{remainder.lstrip('/')}", body))
    except LookupError as exc:
        # An unsigned request is not a fallback. A service that accepted one
        # would accept anybody's.
        logger.error("%s", exc)
        return JsonResponse({"detail": f"{contract.slug} is not configured on this store."}, status=503)

    try:
        answer = requests.request(
            request.method,
            target,
            data=body if body else None,
            headers=headers,
            params=request.GET.dict(),
            timeout=TIMEOUT_SECONDS,
            allow_redirects=False,
        )
    except requests.RequestException as exc:
        # 502, not 500. The store is fine; the thing it forwarded to is not, and
        # the two need different people looking at them.
        logger.warning("Proxying to %s failed: %s", contract.slug, exc)
        return JsonResponse({"detail": f"{contract.slug} did not answer."}, status=502)

    response = HttpResponse(
        answer.content,
        status=answer.status_code,
        content_type=answer.headers.get("Content-Type", "application/octet-stream"),
    )

    for name, value in answer.headers.items():
        if name.lower() not in NEVER_RETURNED:
            response[name] = value

    return response


def _identity_satisfied(request, identity: str) -> bool:
    """
    Whether the caller is who the route requires.

    Enforced by the store, before anything is forwarded. A Feature's service
    deciding for itself whether the caller is staff would be the store trusting
    a third party about its own users.
    """
    if identity == "anonymous":
        return True

    user = getattr(request, "user", None)

    if user is None or not getattr(user, "is_authenticated", False):
        return False

    if identity == "staff":
        return bool(getattr(user, "is_staff", False))

    return True


def _subject(request, identity: str) -> str:
    if identity == "anonymous":
        return ""

    user = getattr(request, "user", None)
    return str(getattr(user, "pk", "") or "")


def _incoming_headers(request) -> dict[str, str]:
    headers = {}

    for key, value in request.META.items():
        if key.startswith("HTTP_"):
            headers[key[5:].replace("_", "-").title()] = value

    if content_type := request.META.get("CONTENT_TYPE"):
        headers["Content-Type"] = content_type

    return headers
