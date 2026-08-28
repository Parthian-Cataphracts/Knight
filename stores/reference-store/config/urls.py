"""
Root URLs for the reference store.

Two surfaces, kept apart on purpose: the shop the public sees, and the
/api/knight/* contract KNIGHT calls. The second is implemented entirely inside
knight_integration and is never mixed into business routing
(docs/store-integration.md §1).
"""

from django.urls import include, path

from knight_integration.external.proxy import proxy_urlpatterns
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

# External Features are proxied rather than mounted: the store forwards a range
# of its own URL space to somebody else's service and returns what comes back,
# running none of their code (docs/adr/0033-api-driven-features.md).
#
# Added after the packages and handed everything already registered, for the
# same reason and with the same rule: the store's own route always wins, and a
# collision is reported rather than discovered by opening the page.
urlpatterns += proxy_urlpatterns(existing=urlpatterns)
