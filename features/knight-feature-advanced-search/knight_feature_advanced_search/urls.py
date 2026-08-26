"""
Routes this feature contributes.

Mounted by the store's loader under the prefix the manifest declares, so the
feature never edits the store's root urlconf.
"""

from django.urls import path

from .views import facets, search, status, suggest

urlpatterns = [
    path("", search, name="knight-search"),
    path("facets/", facets, name="knight-search-facets"),
    path("suggest/", suggest, name="knight-search-suggest"),
    path("status/", status, name="knight-search-status"),
]
