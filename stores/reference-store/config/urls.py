"""
Root URLs for the reference store.

Two surfaces, kept apart on purpose: the shop the public sees, and the
/api/knight/* contract KNIGHT calls. The second is implemented entirely inside
knight_integration and is never mixed into business routing
(docs/store-integration.md §1).
"""

from django.urls import include, path

from knight_integration.features.loader import feature_urlpatterns

urlpatterns = [
    path("", include("apps.shop.urls")),
    path("", include("knight_integration.urls")),
]

# Installed features mount themselves under their own declared prefixes. Added
# last so a feature cannot shadow a route the store already serves — and handed
# what the store already serves, so a collision is reported instead of being
# discovered by opening the page.
urlpatterns += feature_urlpatterns(existing=urlpatterns)
