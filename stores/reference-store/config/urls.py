"""
Root URLs for the reference store.

Two surfaces, kept apart on purpose: the shop the public sees, and the
/api/knight/* contract KNIGHT calls. The second is implemented entirely inside
knight_integration and is never mixed into business routing
(docs/store-integration.md §1).
"""

from django.urls import include, path

urlpatterns = [
    path("", include("apps.shop.urls")),
    path("", include("knight_integration.urls")),
]
