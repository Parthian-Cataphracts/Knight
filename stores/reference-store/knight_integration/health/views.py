"""
The endpoints KNIGHT calls on this store (docs/api-contracts.md §3).

Read-only, all of them. Installation is never performed by an inbound call — it
is executed by the agent from a job it polled for
([`adr/0015`](../../../../docs/adr/0015-feature-delivery-mechanism.md)) — so
nothing here changes anything about the store.

/health, /version and /status require a signed request. The
domain-verification endpoint deliberately does not: it runs before this store
has ever handshaken, so there is no key yet to verify anything with, and the
token it serves exists precisely to be published.
"""

from __future__ import annotations

from django.http import HttpRequest, HttpResponse, JsonResponse
from django.utils import timezone
from django.views.decorators.csrf import csrf_exempt
from django.views.decorators.http import require_GET

from ..conf import get_settings
from ..features.registry import installed_features
from . import checks
from .signature import is_signed_by_knight


def _unauthorised() -> JsonResponse:
    # No detail: a caller learns that it was refused, not what a valid request
    # would have looked like.
    return JsonResponse({"detail": "This endpoint is for the KNIGHT control plane."}, status=401)


@require_GET
@csrf_exempt
def health(request: HttpRequest) -> HttpResponse:
    if not is_signed_by_knight(request):
        return _unauthorised()

    config = get_settings()
    status, dependencies = checks.run_all()

    return JsonResponse(
        {
            "status": status,
            "checkedAt": timezone.now().isoformat().replace("+00:00", "Z"),
            "version": config.store_version,
            "environment": config.environment,
            "dependencies": dependencies,
            # The same block the heartbeat carries. Reported here too so a store
            # KNIGHT polls but which has not heartbeated recently is still
            # certifiable for delivery.
            "runtime": checks.runtime(),
            "features": list(installed_features()),
        }
    )


@require_GET
@csrf_exempt
def version(request: HttpRequest) -> HttpResponse:
    if not is_signed_by_knight(request):
        return _unauthorised()

    config = get_settings()

    return JsonResponse(
        {
            "version": config.store_version,
            "environment": config.environment,
            "deployedAt": None,
        }
    )


@require_GET
@csrf_exempt
def status(request: HttpRequest) -> HttpResponse:
    """A lightweight summary, for dashboards that want a tile rather than a report."""
    if not is_signed_by_knight(request):
        return _unauthorised()

    from ..features import current

    config = get_settings()
    overall, _ = checks.run_all()
    entitlements = current()

    return JsonResponse(
        {
            "status": overall,
            "version": config.store_version,
            "environment": config.environment,
            "installedFeatures": list(installed_features()),
            "entitledFeatures": sorted(entitlements.slugs),
            "entitlementSource": entitlements.source,
        }
    )


@require_GET
@csrf_exempt
def domain_verification(request: HttpRequest) -> HttpResponse:
    """
    Serves the token that proves whoever controls this domain also holds what
    KNIGHT issued for this store.

    Unauthenticated by necessity and by design: it is the bootstrap step, and the
    token is meant to be readable by KNIGHT before any trust exists between the
    two. It is not a credential — publishing it is the entire mechanism — and it
    is useless to anyone who cannot also point the domain at their own server.
    """
    token = get_settings().domain_verification_token

    if not token:
        return HttpResponse(status=404)

    return HttpResponse(token, content_type="text/plain; charset=utf-8")
