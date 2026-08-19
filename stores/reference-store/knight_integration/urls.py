"""
The /api/knight/* contract, plus the domain-verification token.

Mounted at the root of the store so the paths are exactly the ones KNIGHT
expects (docs/api-contracts.md §3). Nothing here is part of the storefront, and
nothing in the storefront routes through it.
"""

from django.urls import path

from .health import views

urlpatterns = [
    path("api/knight/health", views.health, name="knight-health"),
    path("api/knight/version", views.version, name="knight-version"),
    path("api/knight/status", views.status, name="knight-status"),
    path(
        ".well-known/knight-domain-verification",
        views.domain_verification,
        name="knight-domain-verification",
    ),
]
